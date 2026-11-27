#include "sentix/unix_socket_server.hpp"

#include <cerrno>
#include <cstring>
#include <filesystem>
#include <system_error>

#include <fcntl.h>
#include <poll.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/time.h>
#include <sys/un.h>
#include <unistd.h>

#include "sentix/framing.hpp"
#include "sentix/protocol.hpp"

namespace sentix::server {
namespace {

[[noreturn]] void throw_errno(const std::string& what) { throw std::system_error(errno, std::generic_category(), what); }

void set_cloexec(int fd) noexcept {
    const int flags = ::fcntl(fd, F_GETFD);
    if (flags >= 0) {
        ::fcntl(fd, F_SETFD, flags | FD_CLOEXEC);
    }
}

/// Bounded per-read timeout once a frame has started, so a stalled peer cannot pin a worker forever.
void set_receive_timeout(int fd, int seconds) noexcept {
    timeval tv{};
    tv.tv_sec = seconds;
    ::setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
}

}  // namespace

void serve_connection(int fd, const Handler& handler, const FrameErrorFactory& frameError, const std::atomic<bool>& stop) {
    while (ipc::wait_readable(fd, stop)) {
        const auto frame = ipc::read_frame(fd);
        switch (frame.status) {
            case ipc::FrameStatus::Ok: {
                const std::string response = handler(frame.payload);
                if (!ipc::write_frame(fd, response)) {
                    return;
                }
                continue;
            }
            case ipc::FrameStatus::InvalidFrame:
                (void)ipc::write_frame(fd, frameError(protocol::error::kInvalidFrame, "Frame length must be between 1 and 1048576 bytes."));
                return;
            case ipc::FrameStatus::PayloadTooLarge:
                (void)ipc::write_frame(
                    fd, frameError(protocol::error::kPayloadTooLarge,
                                   "Frame length " + std::to_string(frame.declaredLength) + " exceeds the 1048576 byte limit."));
                return;
            case ipc::FrameStatus::Closed:
            case ipc::FrameStatus::IoError:
            default:
                return;
        }
    }
}

UnixSocketServer::UnixSocketServer(std::string socketPath, Handler handler, FrameErrorFactory frameError, std::size_t maxConnections)
    : path_(std::move(socketPath)), handler_(std::move(handler)), frameError_(std::move(frameError)), maxConnections_(maxConnections) {}

UnixSocketServer::~UnixSocketServer() {
    stop();
    {
        const std::lock_guard lock(connectionsMutex_);
        for (auto& connection : connections_) {
            if (connection->thread.joinable()) {
                connection->thread.join();
            }
        }
        connections_.clear();
    }
    close_and_unlink();
    for (int& fd : wakePipe_) {
        if (fd >= 0) {
            ::close(fd);
            fd = -1;
        }
    }
}

void UnixSocketServer::start() {
    sockaddr_un addr{};
    if (path_.empty() || path_.size() >= sizeof(addr.sun_path)) {
        throw std::system_error(std::make_error_code(std::errc::filename_too_long), "socket path empty or too long: " + path_);
    }

    const std::filesystem::path socketPath(path_);
    if (socketPath.has_parent_path()) {
        std::filesystem::create_directories(socketPath.parent_path());
    }

    // Remove a stale socket from a previous run — but never delete a non-socket file.
    struct stat st{};
    if (::lstat(path_.c_str(), &st) == 0) {
        if (!S_ISSOCK(st.st_mode)) {
            throw std::system_error(std::make_error_code(std::errc::file_exists), "refusing to replace non-socket file: " + path_);
        }
        if (::unlink(path_.c_str()) != 0) {
            throw_errno("unlink stale socket " + path_);
        }
    }

    if (::pipe(wakePipe_) != 0) {
        throw_errno("pipe");
    }
    set_cloexec(wakePipe_[0]);
    set_cloexec(wakePipe_[1]);
    ::fcntl(wakePipe_[1], F_SETFL, ::fcntl(wakePipe_[1], F_GETFL) | O_NONBLOCK);

    listenFd_ = ::socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0);
    if (listenFd_ < 0) {
        throw_errno("socket(AF_UNIX, SOCK_STREAM)");
    }

    addr.sun_family = AF_UNIX;
    std::memcpy(addr.sun_path, path_.c_str(), path_.size() + 1);
    if (::bind(listenFd_, reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) != 0) {
        const int err = errno;
        ::close(listenFd_);
        listenFd_ = -1;
        throw std::system_error(err, std::generic_category(), "bind " + path_);
    }
    if (::chmod(path_.c_str(), static_cast<mode_t>(kSocketPermissions)) != 0) {
        const int err = errno;
        close_and_unlink();
        throw std::system_error(err, std::generic_category(), "chmod 0660 " + path_);
    }
    if (::listen(listenFd_, 16) != 0) {
        const int err = errno;
        close_and_unlink();
        throw std::system_error(err, std::generic_category(), "listen " + path_);
    }
}

void UnixSocketServer::run() {
    if (listenFd_ < 0) {
        throw std::logic_error("UnixSocketServer::run() called before start()");
    }

    while (!stop_.load()) {
        pollfd fds[2] = {{listenFd_, POLLIN, 0}, {wakePipe_[0], POLLIN, 0}};
        const int rc = ::poll(fds, 2, 500);
        reap_finished_connections();
        if (rc < 0) {
            if (errno == EINTR) {
                continue;
            }
            break;
        }
        if ((fds[1].revents & POLLIN) != 0) {
            stop_.store(true);
            break;
        }
        if ((fds[0].revents & POLLIN) == 0) {
            continue;
        }

        const int client = ::accept4(listenFd_, nullptr, nullptr, SOCK_CLOEXEC);
        if (client < 0) {
            continue;  // EINTR / ECONNABORTED / EMFILE: keep serving
        }

        const std::lock_guard lock(connectionsMutex_);
        if (connections_.size() >= maxConnections_) {
            (void)ipc::write_frame(client, frameError_(protocol::error::kInternalError, "connection limit reached"));
            ::close(client);
            continue;
        }
        set_receive_timeout(client, 5);
        auto connection = std::make_unique<Connection>();
        Connection* raw = connection.get();
        connection->thread = std::thread([this, client, raw] {
            serve_connection(client, handler_, frameError_, stop_);
            ::shutdown(client, SHUT_RDWR);
            ::close(client);
            raw->done.store(true);
        });
        connections_.push_back(std::move(connection));
    }

    stop_.store(true);
    {
        const std::lock_guard lock(connectionsMutex_);
        for (auto& connection : connections_) {
            if (connection->thread.joinable()) {
                connection->thread.join();
            }
        }
        connections_.clear();
    }
    close_and_unlink();
}

void UnixSocketServer::stop() noexcept {
    stop_.store(true);
    request_stop_from_signal();
}

void UnixSocketServer::request_stop_from_signal() noexcept {
    if (wakePipe_[1] >= 0) {
        const char byte = 'x';
        [[maybe_unused]] const auto n = ::write(wakePipe_[1], &byte, 1);
    }
}

void UnixSocketServer::reap_finished_connections() {
    const std::lock_guard lock(connectionsMutex_);
    for (auto it = connections_.begin(); it != connections_.end();) {
        if ((*it)->done.load()) {
            (*it)->thread.join();
            it = connections_.erase(it);
        } else {
            ++it;
        }
    }
}

void UnixSocketServer::close_and_unlink() noexcept {
    if (listenFd_ >= 0) {
        ::close(listenFd_);
        listenFd_ = -1;
        ::unlink(path_.c_str());
    }
}

}  // namespace sentix::server
