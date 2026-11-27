// Shared helpers for the sentix-native GoogleTest suite (Linux only).
#pragma once

#include <atomic>
#include <chrono>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <memory>
#include <string>
#include <string_view>

#include <nlohmann/json.hpp>
#include <sys/socket.h>
#include <sys/un.h>
#include <unistd.h>

#include "sentix/framing.hpp"
#include "sentix/protocol.hpp"

namespace sentix::testing {

inline const char* universe_json_path() { return SENTIX_TEST_UNIVERSE_JSON; }

/// Unique scratch directory under /tmp, removed on destruction.
class TempDir {
public:
    TempDir() {
        static std::atomic<int> counter{0};
        path_ = std::filesystem::temp_directory_path() /
                ("sentix-test-" + std::to_string(::getpid()) + "-" + std::to_string(counter.fetch_add(1)));
        std::filesystem::remove_all(path_);
        std::filesystem::create_directories(path_);
    }
    ~TempDir() {
        std::error_code ec;
        std::filesystem::remove_all(path_, ec);
    }
    TempDir(const TempDir&) = delete;
    TempDir& operator=(const TempDir&) = delete;

    [[nodiscard]] const std::filesystem::path& path() const noexcept { return path_; }

    std::filesystem::path write(const std::string& name, std::string_view content) const {
        const auto file = path_ / name;
        std::ofstream(file, std::ios::binary) << content;
        return file;
    }

private:
    std::filesystem::path path_;
};

inline std::string read_file(const std::filesystem::path& file) {
    std::ifstream in(file, std::ios::binary);
    return {std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>()};
}

/// Controllable clocks for deterministic dedup / uptime tests.
struct FakeClocks {
    std::shared_ptr<std::chrono::steady_clock::time_point> steady =
        std::make_shared<std::chrono::steady_clock::time_point>(std::chrono::steady_clock::time_point{} + std::chrono::hours(1000));
    std::shared_ptr<std::chrono::system_clock::time_point> system = std::make_shared<std::chrono::system_clock::time_point>(
        std::chrono::system_clock::time_point{} + std::chrono::milliseconds(1'795'700'000'123LL));

    [[nodiscard]] protocol::Clocks clocks() const {
        auto s = steady;
        auto y = system;
        return protocol::Clocks{[s] { return *s; }, [y] { return *y; }};
    }

    void advance(std::chrono::steady_clock::duration d) const { *steady += d; }
};

inline nlohmann::json envelope(std::string_view type, nlohmann::json payload = nlohmann::json::object(),
                               std::string_view correlationId = "550e8400-e29b-41d4-a716-446655440000") {
    return nlohmann::json{
        {"protocolVersion", 1},
        {"correlationId", std::string(correlationId)},
        {"type", std::string(type)},
        {"timestampUtc", "2026-11-26T14:30:05.123Z"},
        {"payload", std::move(payload)},
    };
}

inline nlohmann::json market_event(std::string_view eventId, std::string_view headline, std::string_view body = "",
                                   std::string_view summary = "", std::string_view source = "ALPACA_BENZINGA",
                                   std::string_view author = "Benzinga", nlohmann::json sourceTickers = nlohmann::json::array()) {
    return nlohmann::json{
        {"eventId", std::string(eventId)},
        {"source", std::string(source)},
        {"author", std::string(author)},
        {"publishedAtUtc", "2026-11-26T14:30:00Z"},
        {"headline", std::string(headline)},
        {"summary", std::string(summary)},
        {"body", std::string(body)},
        {"sourceTickers", std::move(sourceTickers)},
    };
}

/// RAII file descriptor.
class Fd {
public:
    explicit Fd(int fd = -1) noexcept : fd_(fd) {}
    ~Fd() { reset(); }
    Fd(Fd&& other) noexcept : fd_(other.release()) {}
    Fd& operator=(Fd&& other) noexcept {
        if (this != &other) {
            reset(other.release());
        }
        return *this;
    }
    Fd(const Fd&) = delete;
    Fd& operator=(const Fd&) = delete;

    [[nodiscard]] int get() const noexcept { return fd_; }
    int release() noexcept {
        const int fd = fd_;
        fd_ = -1;
        return fd;
    }
    void reset(int fd = -1) noexcept {
        if (fd_ >= 0) {
            ::close(fd_);
        }
        fd_ = fd;
    }

private:
    int fd_;
};

inline Fd connect_unix(const std::string& path) {
    Fd fd(::socket(AF_UNIX, SOCK_STREAM | SOCK_CLOEXEC, 0));
    sockaddr_un addr{};
    addr.sun_family = AF_UNIX;
    path.copy(addr.sun_path, sizeof(addr.sun_path) - 1);
    if (::connect(fd.get(), reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)) != 0) {
        return Fd{};
    }
    timeval tv{};
    tv.tv_sec = 5;
    ::setsockopt(fd.get(), SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    return fd;
}

/// Sends one request frame and returns the parsed response envelope (null json on failure).
inline nlohmann::json roundtrip(int fd, const nlohmann::json& request) {
    if (!ipc::write_frame(fd, request.dump())) {
        return nullptr;
    }
    const auto frame = ipc::read_frame(fd);
    if (frame.status != ipc::FrameStatus::Ok) {
        return nullptr;
    }
    return nlohmann::json::parse(frame.payload);
}

}  // namespace sentix::testing
