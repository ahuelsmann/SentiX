#include <gtest/gtest.h>

#include <array>
#include <cstdint>
#include <stdexcept>
#include <string>

#include <sys/socket.h>
#include <unistd.h>

#include "sentix/framing.hpp"
#include "test_helpers.hpp"

using sentix::ipc::FrameStatus;
using sentix::testing::Fd;

namespace {

struct SocketPair {
    Fd a;
    Fd b;
    SocketPair() {
        int fds[2];
        EXPECT_EQ(::socketpair(AF_UNIX, SOCK_STREAM, 0, fds), 0);
        a.reset(fds[0]);
        b.reset(fds[1]);
        timeval tv{};
        tv.tv_sec = 2;
        ::setsockopt(a.get(), SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
        ::setsockopt(b.get(), SOL_SOCKET, SO_RCVTIMEO, &tv, sizeof(tv));
    }
};

void send_raw(int fd, const void* data, std::size_t size) { ASSERT_TRUE(sentix::ipc::write_all(fd, data, size)); }

void send_header(int fd, std::uint32_t length) {
    const auto header = sentix::ipc::encode_length_le(length);
    send_raw(fd, header.data(), header.size());
}

}  // namespace

TEST(Framing, EncodesLengthAsLittleEndian) {
    const auto bytes = sentix::ipc::encode_length_le(0x01020304U);
    EXPECT_EQ(bytes[0], 0x04);
    EXPECT_EQ(bytes[1], 0x03);
    EXPECT_EQ(bytes[2], 0x02);
    EXPECT_EQ(bytes[3], 0x01);

    const auto max = sentix::ipc::encode_length_le(1'048'576U);  // 0x00100000
    EXPECT_EQ(max, (std::array<std::uint8_t, 4>{0x00, 0x00, 0x10, 0x00}));
}

TEST(Framing, DecodeIsInverseOfEncode) {
    for (const std::uint32_t value : {0U, 1U, 255U, 256U, 65'535U, 1'048'576U, 1'048'577U, 0xFFFFFFFFU}) {
        EXPECT_EQ(sentix::ipc::decode_length_le(sentix::ipc::encode_length_le(value)), value);
    }
}

TEST(Framing, EncodeFrameProducesHeaderPlusPayload) {
    const std::string frame = sentix::ipc::encode_frame("{}");
    ASSERT_EQ(frame.size(), 6U);
    EXPECT_EQ(frame.substr(0, 4), std::string("\x02\x00\x00\x00", 4));
    EXPECT_EQ(frame.substr(4), "{}");
}

TEST(Framing, EncodeFrameRejectsEmptyAndOversizedPayloads) {
    EXPECT_THROW((void)sentix::ipc::encode_frame(""), std::length_error);
    EXPECT_THROW((void)sentix::ipc::encode_frame(std::string(sentix::ipc::kMaxFrameBytes + 1, 'x')), std::length_error);
    EXPECT_NO_THROW((void)sentix::ipc::encode_frame(std::string(sentix::ipc::kMaxFrameBytes, 'x')));
}

TEST(Framing, ClassifiesLengthBoundaries) {
    EXPECT_EQ(sentix::ipc::kMaxFrameBytes, 1'048'576U);
    EXPECT_EQ(sentix::ipc::classify_length(0U), FrameStatus::InvalidFrame);
    EXPECT_EQ(sentix::ipc::classify_length(1U), FrameStatus::Ok);
    EXPECT_EQ(sentix::ipc::classify_length(1'048'576U), FrameStatus::Ok);
    EXPECT_EQ(sentix::ipc::classify_length(1'048'577U), FrameStatus::PayloadTooLarge);
    EXPECT_EQ(sentix::ipc::classify_length(0xFFFFFFFFU), FrameStatus::PayloadTooLarge);
}

TEST(Framing, ReadsCompleteFrameOverSocketPair) {
    SocketPair pair;
    ASSERT_TRUE(sentix::ipc::write_frame(pair.a.get(), R"({"hello":"world"})"));

    const auto frame = sentix::ipc::read_frame(pair.b.get());
    EXPECT_EQ(frame.status, FrameStatus::Ok);
    EXPECT_EQ(frame.declaredLength, 17U);
    EXPECT_EQ(frame.payload, R"({"hello":"world"})");
}

TEST(Framing, ReadsMaximumSizedFrame) {
    SocketPair pair;
    const std::string payload(sentix::ipc::kMaxFrameBytes, 'a');
    std::thread writer([&] { EXPECT_TRUE(sentix::ipc::write_frame(pair.a.get(), payload)); });

    const auto frame = sentix::ipc::read_frame(pair.b.get());
    writer.join();
    EXPECT_EQ(frame.status, FrameStatus::Ok);
    EXPECT_EQ(frame.payload.size(), sentix::ipc::kMaxFrameBytes);
}

TEST(Framing, ZeroLengthIsInvalidFrame) {
    SocketPair pair;
    send_header(pair.a.get(), 0U);

    const auto frame = sentix::ipc::read_frame(pair.b.get());
    EXPECT_EQ(frame.status, FrameStatus::InvalidFrame);
    EXPECT_TRUE(frame.payload.empty());
}

TEST(Framing, OversizedLengthIsRejectedWithoutReadingPayload) {
    SocketPair pair;
    send_header(pair.a.get(), 1'048'577U);
    const std::string trailing = "TRAILING";
    send_raw(pair.a.get(), trailing.data(), trailing.size());

    const auto frame = sentix::ipc::read_frame(pair.b.get());
    EXPECT_EQ(frame.status, FrameStatus::PayloadTooLarge);
    EXPECT_EQ(frame.declaredLength, 1'048'577U);
    EXPECT_TRUE(frame.payload.empty());

    // The overflowing bytes were never consumed into a buffer.
    std::array<char, 8> rest{};
    EXPECT_EQ(sentix::ipc::read_exact(pair.b.get(), rest.data(), rest.size()), rest.size());
    EXPECT_EQ(std::string(rest.data(), rest.size()), trailing);
}

TEST(Framing, OrderlyCloseBeforeHeaderIsClosed) {
    SocketPair pair;
    pair.a.reset();
    EXPECT_EQ(sentix::ipc::read_frame(pair.b.get()).status, FrameStatus::Closed);
}

TEST(Framing, PartialHeaderIsIoError) {
    SocketPair pair;
    const std::uint8_t two[2] = {0x05, 0x00};
    send_raw(pair.a.get(), two, sizeof(two));
    pair.a.reset();
    EXPECT_EQ(sentix::ipc::read_frame(pair.b.get()).status, FrameStatus::IoError);
}

TEST(Framing, TruncatedPayloadIsIoError) {
    SocketPair pair;
    send_header(pair.a.get(), 10U);
    send_raw(pair.a.get(), "abc", 3);
    pair.a.reset();

    const auto frame = sentix::ipc::read_frame(pair.b.get());
    EXPECT_EQ(frame.status, FrameStatus::IoError);
    EXPECT_TRUE(frame.payload.empty());
}

TEST(Framing, WriteFrameRejectsInvalidSizes) {
    SocketPair pair;
    EXPECT_FALSE(sentix::ipc::write_frame(pair.a.get(), ""));
    EXPECT_FALSE(sentix::ipc::write_frame(pair.a.get(), std::string(sentix::ipc::kMaxFrameBytes + 1, 'x')));
}

TEST(Framing, WriteToClosedPeerFailsWithoutSigpipe) {
    SocketPair pair;
    pair.b.reset();
    EXPECT_FALSE(sentix::ipc::write_frame(pair.a.get(), std::string(1 << 20, 'x')));
}

TEST(Framing, WaitReadableHonoursStopFlag) {
    SocketPair pair;
    std::atomic<bool> stop{true};
    EXPECT_FALSE(sentix::ipc::wait_readable(pair.b.get(), stop, 10));

    stop = false;
    ASSERT_TRUE(sentix::ipc::write_frame(pair.a.get(), "x"));
    EXPECT_TRUE(sentix::ipc::wait_readable(pair.b.get(), stop, 10));
}
