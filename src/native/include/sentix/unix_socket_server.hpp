// SentiX native fast-path — POSIX AF_UNIX/SOCK_STREAM server (specs/002 §1). Linux only.
#pragma once

#include <atomic>
#include <cstddef>
#include <functional>
#include <list>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>

namespace sentix::server {

inline constexpr std::string_view kDefaultSocketPath = "/run/sentix/native.sock";
inline constexpr unsigned kSocketPermissions = 0660;

/// Request payload -> response payload.
using Handler = std::function<std::string(std::string_view)>;
/// (code, message) -> ERROR response payload for framing failures.
using FrameErrorFactory = std::function<std::string(std::string_view, std::string_view)>;

/// Serves request/response frames on one connected socket until EOF, a framing error, or `stop`.
/// - length 0           -> ERROR INVALID_FRAME, close
/// - length > 1 MiB     -> ERROR PAYLOAD_TOO_LARGE, close immediately (oversized bytes are never read)
void serve_connection(int fd, const Handler& handler, const FrameErrorFactory& frameError, const std::atomic<bool>& stop);

class UnixSocketServer {
public:
    UnixSocketServer(std::string socketPath, Handler handler, FrameErrorFactory frameError, std::size_t maxConnections = 16);
    ~UnixSocketServer();

    UnixSocketServer(const UnixSocketServer&) = delete;
    UnixSocketServer& operator=(const UnixSocketServer&) = delete;
    UnixSocketServer(UnixSocketServer&&) = delete;
    UnixSocketServer& operator=(UnixSocketServer&&) = delete;

    /// Creates the parent directory, unlinks a stale socket, bind(), listen(), chmod 0660. Throws std::system_error.
    void start();

    /// Accept loop; returns after stop()/request_stop_from_signal(). Joins all connection threads and unlinks the socket.
    void run();

    /// Thread-safe stop request.
    void stop() noexcept;

    /// Async-signal-safe stop request (only performs write(2) on the wake pipe).
    void request_stop_from_signal() noexcept;

    [[nodiscard]] const std::string& path() const noexcept { return path_; }
    [[nodiscard]] bool is_listening() const noexcept { return listenFd_ >= 0; }

private:
    struct Connection {
        std::thread thread;
        std::atomic<bool> done{false};
    };

    void reap_finished_connections();
    void close_and_unlink() noexcept;

    std::string path_;
    Handler handler_;
    FrameErrorFactory frameError_;
    std::size_t maxConnections_;
    int listenFd_{-1};
    int wakePipe_[2]{-1, -1};
    std::atomic<bool> stop_{false};
    std::list<std::unique_ptr<Connection>> connections_;
    std::mutex connectionsMutex_;
};

}  // namespace sentix::server
