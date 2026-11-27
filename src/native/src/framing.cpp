#include "sentix/framing.hpp"

#include <cerrno>
#include <stdexcept>

#include <poll.h>
#include <sys/socket.h>
#include <sys/types.h>
#include <unistd.h>

namespace sentix::ipc {

std::array<std::uint8_t, kHeaderBytes> encode_length_le(std::uint32_t length) noexcept {
    return {
        static_cast<std::uint8_t>(length & 0xFFU),
        static_cast<std::uint8_t>((length >> 8U) & 0xFFU),
        static_cast<std::uint8_t>((length >> 16U) & 0xFFU),
        static_cast<std::uint8_t>((length >> 24U) & 0xFFU),
    };
}

std::uint32_t decode_length_le(std::span<const std::uint8_t, kHeaderBytes> bytes) noexcept {
    return static_cast<std::uint32_t>(bytes[0]) | (static_cast<std::uint32_t>(bytes[1]) << 8U) |
           (static_cast<std::uint32_t>(bytes[2]) << 16U) | (static_cast<std::uint32_t>(bytes[3]) << 24U);
}

std::string encode_frame(std::string_view payload) {
    if (payload.empty() || payload.size() > kMaxFrameBytes) {
        throw std::length_error("frame payload must be between 1 and 1048576 bytes");
    }
    const auto header = encode_length_le(static_cast<std::uint32_t>(payload.size()));
    std::string frame;
    frame.reserve(kHeaderBytes + payload.size());
    for (const auto byte : header) {
        frame.push_back(static_cast<char>(byte));
    }
    frame.append(payload);
    return frame;
}

FrameStatus classify_length(std::uint32_t declaredLength) noexcept {
    if (declaredLength == 0U) {
        return FrameStatus::InvalidFrame;
    }
    if (declaredLength > kMaxFrameBytes) {
        return FrameStatus::PayloadTooLarge;
    }
    return FrameStatus::Ok;
}

std::size_t read_exact(int fd, void* buffer, std::size_t size) noexcept {
    auto* out = static_cast<std::uint8_t*>(buffer);
    std::size_t total = 0;
    while (total < size) {
        const ssize_t n = ::recv(fd, out + total, size - total, 0);
        if (n > 0) {
            total += static_cast<std::size_t>(n);
            continue;
        }
        if (n < 0 && errno == EINTR) {
            continue;
        }
        break;  // EOF, timeout (EAGAIN) or hard error
    }
    return total;
}

bool write_all(int fd, const void* buffer, std::size_t size) noexcept {
    const auto* in = static_cast<const std::uint8_t*>(buffer);
    std::size_t total = 0;
    while (total < size) {
        const ssize_t n = ::send(fd, in + total, size - total, MSG_NOSIGNAL);
        if (n > 0) {
            total += static_cast<std::size_t>(n);
            continue;
        }
        if (n < 0 && errno == EINTR) {
            continue;
        }
        return false;
    }
    return true;
}

FrameReadResult read_frame(int fd) {
    FrameReadResult result;
    std::array<std::uint8_t, kHeaderBytes> header{};
    const std::size_t got = read_exact(fd, header.data(), header.size());
    if (got == 0) {
        result.status = FrameStatus::Closed;
        return result;
    }
    if (got != header.size()) {
        result.status = FrameStatus::IoError;
        return result;
    }

    result.declaredLength = decode_length_le(header);
    result.status = classify_length(result.declaredLength);
    if (result.status != FrameStatus::Ok) {
        return result;  // never read or buffer an invalid/oversized payload
    }

    result.payload.resize(result.declaredLength);
    if (read_exact(fd, result.payload.data(), result.payload.size()) != result.payload.size()) {
        result.payload.clear();
        result.status = FrameStatus::IoError;
    }
    return result;
}

bool write_frame(int fd, std::string_view payload) noexcept {
    if (payload.empty() || payload.size() > kMaxFrameBytes) {
        return false;
    }
    const auto header = encode_length_le(static_cast<std::uint32_t>(payload.size()));
    return write_all(fd, header.data(), header.size()) && write_all(fd, payload.data(), payload.size());
}

bool wait_readable(int fd, const std::atomic<bool>& stop, int sliceMs) noexcept {
    pollfd pfd{fd, POLLIN, 0};
    while (!stop.load(std::memory_order_relaxed)) {
        pfd.revents = 0;
        const int rc = ::poll(&pfd, 1, sliceMs);
        if (rc > 0) {
            // POLLHUP/POLLERR still let the subsequent read observe EOF/error deterministically.
            return true;
        }
        if (rc < 0 && errno != EINTR) {
            return false;
        }
    }
    return false;
}

}  // namespace sentix::ipc
