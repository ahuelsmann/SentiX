// SentiX native fast-path — IPC framing (specs/002-ipc-contract.md §1).
// Frame = 4-byte uint32 little-endian payload length N + N bytes UTF-8 JSON, 1 <= N <= 1 MiB.
#pragma once

#include <array>
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <span>
#include <string>
#include <string_view>

namespace sentix::ipc {

inline constexpr std::size_t kHeaderBytes = 4;
inline constexpr std::uint32_t kMaxFrameBytes = 1'048'576U;

/// Encodes a length as 4 little-endian bytes (independent of host endianness).
[[nodiscard]] std::array<std::uint8_t, kHeaderBytes> encode_length_le(std::uint32_t length) noexcept;

/// Decodes 4 little-endian bytes into a length (independent of host endianness).
[[nodiscard]] std::uint32_t decode_length_le(std::span<const std::uint8_t, kHeaderBytes> bytes) noexcept;

/// Builds a complete frame (header + payload). Throws std::length_error when payload is empty or > kMaxFrameBytes.
[[nodiscard]] std::string encode_frame(std::string_view payload);

enum class FrameStatus {
    Ok,               ///< payload holds exactly the declared number of bytes
    Closed,           ///< orderly EOF before any header byte
    InvalidFrame,     ///< declared length == 0
    PayloadTooLarge,  ///< declared length > kMaxFrameBytes (payload bytes NOT read)
    IoError,          ///< read error, timeout, or EOF mid-frame
};

struct FrameReadResult {
    FrameStatus status{FrameStatus::IoError};
    std::uint32_t declaredLength{0};
    std::string payload;
};

/// Validates a declared frame length.
[[nodiscard]] FrameStatus classify_length(std::uint32_t declaredLength) noexcept;

/// Reads exactly `size` bytes (retries on EINTR). Returns the number of bytes read (< size on EOF/error).
std::size_t read_exact(int fd, void* buffer, std::size_t size) noexcept;

/// Writes all bytes (retries on EINTR / partial writes, never raises SIGPIPE).
[[nodiscard]] bool write_all(int fd, const void* buffer, std::size_t size) noexcept;

/// Blocking read of one frame. Never allocates more than kMaxFrameBytes.
[[nodiscard]] FrameReadResult read_frame(int fd);

/// Writes one frame. Returns false on I/O failure or invalid payload size.
[[nodiscard]] bool write_frame(int fd, std::string_view payload) noexcept;

/// Polls `fd` for readability in `sliceMs` slices until data arrives (true) or `stop` is set / the fd errors (false).
[[nodiscard]] bool wait_readable(int fd, const std::atomic<bool>& stop, int sliceMs = 200) noexcept;

}  // namespace sentix::ipc
