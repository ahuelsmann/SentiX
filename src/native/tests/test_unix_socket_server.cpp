#include <gtest/gtest.h>

#include <array>
#include <chrono>
#include <csignal>
#include <filesystem>
#include <memory>
#include <string>
#include <thread>
#include <utility>
#include <vector>

#include <nlohmann/json.hpp>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/un.h>
#include <sys/wait.h>
#include <unistd.h>

#include "sentix/framing.hpp"
#include "sentix/protocol.hpp"
#include "sentix/sentiment_engine.hpp"
#include "sentix/unix_socket_server.hpp"
#include "test_helpers.hpp"

using namespace std::chrono_literals;
using nlohmann::json;
using sentix::server::UnixSocketServer;
using sentix::testing::connect_unix;
using sentix::testing::envelope;
using sentix::testing::Fd;
using sentix::testing::market_event;
using sentix::testing::roundtrip;

namespace {

bool is_unix_socket_file(const std::filesystem::path& p) {
    struct stat st{};
    return ::lstat(p.c_str(), &st) == 0 && S_ISSOCK(st.st_mode);
}

unsigned permissions(const std::filesystem::path& p) {
    struct stat st{};
    return ::lstat(p.c_str(), &st) == 0 ? static_cast<unsigned>(st.st_mode & 0777U) : 0U;
}

/// Starts a real AF_UNIX server on a /tmp path with the production RequestHandler on a background thread.
class LiveServer {
public:
    explicit LiveServer(const std::filesystem::path& socketPath, const std::filesystem::path& configPath, std::size_t maxConnections = 16)
        : handler_(configPath, std::make_unique<sentix::sentiment::MockSentimentEngine>()),
          server_(
              socketPath.string(), [this](std::string_view r) { return handler_.handle(r); },
              [this](std::string_view c, std::string_view m) { return handler_.frame_error(c, m); }, maxConnections) {
        handler_.load_initial_config();
        server_.start();
        thread_ = std::thread([this] { server_.run(); });
    }
    ~LiveServer() { shutdown(); }
    LiveServer(const LiveServer&) = delete;
    LiveServer& operator=(const LiveServer&) = delete;

    void shutdown() {
        server_.stop();
        if (thread_.joinable()) {
            thread_.join();
        }
    }

    sentix::protocol::RequestHandler& handler() { return handler_; }

private:
    sentix::protocol::RequestHandler handler_;
    UnixSocketServer server_;
    std::thread thread_;
};

class UnixSocketServerTest : public ::testing::Test {
protected:
    sentix::testing::TempDir dir;
    std::filesystem::path configPath = dir.write("universe.json", sentix::testing::read_file(sentix::testing::universe_json_path()));
    std::filesystem::path socketPath = dir.path() / "run" / "sentix" / "native.sock";
};

json read_response(int fd) {
    const auto frame = sentix::ipc::read_frame(fd);
    EXPECT_EQ(frame.status, sentix::ipc::FrameStatus::Ok);
    return frame.status == sentix::ipc::FrameStatus::Ok ? json::parse(frame.payload) : json(nullptr);
}

bool peer_closed(int fd) {
    char byte = 0;
    return ::recv(fd, &byte, 1, 0) == 0;
}

}  // namespace

TEST_F(UnixSocketServerTest, CreatesParentDirectoryAndSocketWith0660) {
    LiveServer server(socketPath, configPath);
    EXPECT_TRUE(std::filesystem::is_directory(socketPath.parent_path()));
    EXPECT_TRUE(is_unix_socket_file(socketPath));
    EXPECT_EQ(permissions(socketPath), 0660U);
}

TEST_F(UnixSocketServerTest, HealthCheckRoundtripOverRealSocket) {
    LiveServer server(socketPath, configPath);
    auto client = connect_unix(socketPath.string());
    ASSERT_GE(client.get(), 0);

    const auto response = roundtrip(client.get(), envelope("HEALTH_CHECK", json::object(), "corr-1"));
    ASSERT_FALSE(response.is_null());
    EXPECT_EQ(response.at("type"), "HEALTH_CHECK_ACK");
    EXPECT_EQ(response.at("correlationId"), "corr-1");
    EXPECT_EQ(response.at("payload").at("status"), "READY");
    EXPECT_EQ(response.at("payload").at("engineVersion"), "0.1.0");
}

TEST_F(UnixSocketServerTest, PersistentConnectionServesSequentialRequests) {
    LiveServer server(socketPath, configPath);
    auto client = connect_unix(socketPath.string());
    ASSERT_GE(client.get(), 0);

    const auto score = roundtrip(client.get(), envelope("SCORE_EVENT", market_event("evt_1", "Apple beats record", "<p>Strong demand</p>"),
                                                        "corr-score"));
    ASSERT_FALSE(score.is_null());
    EXPECT_EQ(score.at("type"), "SCORE_EVENT_RESULT");
    EXPECT_EQ(score.at("payload").at("passToDecisionSwarm"), true);

    const auto dup = roundtrip(client.get(), envelope("SCORE_EVENT", market_event("evt_2", "apple BEATS record", "Strong demand")));
    EXPECT_EQ(dup.at("payload").at("dropReason"), "DUPLICATE");

    const auto reload = roundtrip(client.get(), envelope("RELOAD_CONFIG"));
    EXPECT_EQ(reload.at("type"), "RELOAD_CONFIG_ACK");

    const auto health = roundtrip(client.get(), envelope("HEALTH_CHECK"));
    EXPECT_EQ(health.at("payload").at("processedEventsTotal"), 2);
}

TEST_F(UnixSocketServerTest, ConcurrentClientsAreServed) {
    LiveServer server(socketPath, configPath);
    std::vector<std::thread> clients;
    std::atomic<int> ok{0};
    for (int i = 0; i < 4; ++i) {
        clients.emplace_back([&, i] {
            auto fd = connect_unix(socketPath.string());
            for (int j = 0; j < 25; ++j) {
                const auto r = roundtrip(fd.get(), envelope("SCORE_EVENT", market_event("e" + std::to_string(i * 100 + j),
                                                                                         "Nvidia jumps " + std::to_string(i * 100 + j))));
                if (!r.is_null() && r.at("type") == "SCORE_EVENT_RESULT") {
                    ++ok;
                }
            }
        });
    }
    for (auto& t : clients) {
        t.join();
    }
    EXPECT_EQ(ok.load(), 100);
    EXPECT_EQ(server.handler().processed_events_total(), 100U);
}

TEST_F(UnixSocketServerTest, OversizedFrameGetsPayloadTooLargeAndConnectionCloses) {
    LiveServer server(socketPath, configPath);
    auto client = connect_unix(socketPath.string());
    ASSERT_GE(client.get(), 0);

    const auto header = sentix::ipc::encode_length_le(sentix::ipc::kMaxFrameBytes + 1U);
    ASSERT_TRUE(sentix::ipc::write_all(client.get(), header.data(), header.size()));

    const auto response = read_response(client.get());
    ASSERT_FALSE(response.is_null());
    EXPECT_EQ(response.at("type"), "ERROR");
    EXPECT_EQ(response.at("payload").at("code"), "PAYLOAD_TOO_LARGE");
    EXPECT_EQ(response.at("payload").at("retryable"), false);
    EXPECT_TRUE(peer_closed(client.get()));
}

TEST_F(UnixSocketServerTest, HugeDeclaredLengthIsRejectedImmediately) {
    LiveServer server(socketPath, configPath);
    auto client = connect_unix(socketPath.string());
    const auto header = sentix::ipc::encode_length_le(0xFFFFFFFFU);
    ASSERT_TRUE(sentix::ipc::write_all(client.get(), header.data(), header.size()));

    const auto started = std::chrono::steady_clock::now();
    const auto response = read_response(client.get());
    EXPECT_LT(std::chrono::steady_clock::now() - started, 2s);
    EXPECT_EQ(response.at("payload").at("code"), "PAYLOAD_TOO_LARGE");
    EXPECT_TRUE(peer_closed(client.get()));
}

TEST_F(UnixSocketServerTest, ExactlyOneMebibyteFrameIsAccepted) {
    LiveServer server(socketPath, configPath);
    auto client = connect_unix(socketPath.string());

    auto request = envelope("SCORE_EVENT", market_event("big", "Apple beats record", ""));
    const std::string skeleton = request.dump();
    request["payload"]["body"] = std::string(sentix::ipc::kMaxFrameBytes - skeleton.size(), 'a');
    const std::string raw = request.dump();
    ASSERT_EQ(raw.size(), sentix::ipc::kMaxFrameBytes);

    ASSERT_TRUE(sentix::ipc::write_frame(client.get(), raw));
    const auto response = read_response(client.get());
    EXPECT_EQ(response.at("type"), "SCORE_EVENT_RESULT");
}

TEST_F(UnixSocketServerTest, ZeroLengthFrameGetsInvalidFrameAndConnectionCloses) {
    LiveServer server(socketPath, configPath);
    auto client = connect_unix(socketPath.string());
    const auto header = sentix::ipc::encode_length_le(0U);
    ASSERT_TRUE(sentix::ipc::write_all(client.get(), header.data(), header.size()));

    const auto response = read_response(client.get());
    EXPECT_EQ(response.at("payload").at("code"), "INVALID_FRAME");
    EXPECT_EQ(response.at("payload").at("message"), "Frame length must be between 1 and 1048576 bytes.");
    EXPECT_TRUE(peer_closed(client.get()));
}

TEST_F(UnixSocketServerTest, InvalidJsonKeepsConnectionOpen) {
    LiveServer server(socketPath, configPath);
    auto client = connect_unix(socketPath.string());
    ASSERT_TRUE(sentix::ipc::write_frame(client.get(), "not json"));
    EXPECT_EQ(read_response(client.get()).at("payload").at("code"), "DESERIALIZATION_ERROR");

    EXPECT_EQ(roundtrip(client.get(), envelope("HEALTH_CHECK")).at("type"), "HEALTH_CHECK_ACK");
}

TEST_F(UnixSocketServerTest, ConnectionLimitIsEnforced) {
    LiveServer server(socketPath, configPath, 1);
    auto first = connect_unix(socketPath.string());
    ASSERT_EQ(roundtrip(first.get(), envelope("HEALTH_CHECK")).at("type"), "HEALTH_CHECK_ACK");

    auto second = connect_unix(socketPath.string());
    const auto rejected = read_response(second.get());
    EXPECT_EQ(rejected.at("payload").at("code"), "INTERNAL_ERROR");
    EXPECT_TRUE(peer_closed(second.get()));
}

TEST_F(UnixSocketServerTest, StopUnlinksSocket) {
    {
        LiveServer server(socketPath, configPath);
        ASSERT_TRUE(is_unix_socket_file(socketPath));
        auto client = connect_unix(socketPath.string());  // an idle pooled connection must not block shutdown
        server.shutdown();
        EXPECT_FALSE(std::filesystem::exists(socketPath));
    }
    EXPECT_FALSE(std::filesystem::exists(socketPath));
}

TEST_F(UnixSocketServerTest, StaleSocketIsReplaced) {
    std::filesystem::create_directories(socketPath.parent_path());
    {
        Fd stale(::socket(AF_UNIX, SOCK_STREAM, 0));
        sockaddr_un addr{};
        addr.sun_family = AF_UNIX;
        socketPath.string().copy(addr.sun_path, sizeof(addr.sun_path) - 1);
        ASSERT_EQ(::bind(stale.get(), reinterpret_cast<const sockaddr*>(&addr), sizeof(addr)), 0);
    }  // closed without unlink -> stale socket file remains
    ASSERT_TRUE(is_unix_socket_file(socketPath));

    LiveServer server(socketPath, configPath);
    auto client = connect_unix(socketPath.string());
    EXPECT_EQ(roundtrip(client.get(), envelope("HEALTH_CHECK")).at("type"), "HEALTH_CHECK_ACK");
}

TEST_F(UnixSocketServerTest, RefusesToReplaceRegularFile) {
    std::filesystem::create_directories(socketPath.parent_path());
    dir.write("run/sentix/native.sock", "precious data");

    UnixSocketServer server(socketPath.string(), [](std::string_view) { return std::string("{}"); },
                            [](std::string_view, std::string_view) { return std::string("{}"); });
    EXPECT_THROW(server.start(), std::system_error);
    EXPECT_EQ(sentix::testing::read_file(socketPath), "precious data");
}

TEST_F(UnixSocketServerTest, RejectsOverlongSocketPath) {
    UnixSocketServer server("/tmp/" + std::string(200, 'x') + ".sock", [](std::string_view) { return std::string("{}"); },
                            [](std::string_view, std::string_view) { return std::string("{}"); });
    EXPECT_THROW(server.start(), std::system_error);
    EXPECT_THROW(UnixSocketServer("", {}, {}).start(), std::system_error);
}

TEST_F(UnixSocketServerTest, RunBeforeStartIsLogicError) {
    UnixSocketServer server(socketPath.string(), {}, {});
    EXPECT_THROW(server.run(), std::logic_error);
}

TEST(ServeConnection, WorksOverSocketPair) {
    int fds[2];
    ASSERT_EQ(::socketpair(AF_UNIX, SOCK_STREAM, 0, fds), 0);
    Fd client(fds[0]);
    Fd serverSide(fds[1]);
    std::atomic<bool> stop{false};

    std::thread worker([&] {
        sentix::server::serve_connection(
            serverSide.get(), [](std::string_view request) { return "echo:" + std::string(request); },
            [](std::string_view code, std::string_view) { return std::string(code); }, stop);
    });

    ASSERT_TRUE(sentix::ipc::write_frame(client.get(), "ping"));
    const auto frame = sentix::ipc::read_frame(client.get());
    EXPECT_EQ(frame.payload, "echo:ping");

    client.reset();  // EOF ends the loop
    worker.join();
}

// ---------------------------------------------------------------- end-to-end: real sentix-native binary

TEST(SentixNativeBinary, ServesHealthAndUnlinksSocketOnSigterm) {
    sentix::testing::TempDir dir;
    const auto configPath = dir.write("universe.json", sentix::testing::read_file(sentix::testing::universe_json_path()));
    const auto socketPath = dir.path() / "run" / "native.sock";

    const pid_t pid = ::fork();
    ASSERT_GE(pid, 0);
    if (pid == 0) {
        ::setenv("SENTIX_SOCKET_PATH", socketPath.c_str(), 1);
        ::setenv("SENTIX_CONFIG_PATH", configPath.c_str(), 1);
        ::setenv("SENTIX_ENGINE_MODE", "mock", 1);  // independent of any /models present on the host
        ::execl(SENTIX_NATIVE_BINARY, "sentix-native", static_cast<char*>(nullptr));
        ::_exit(127);
    }

    Fd client;
    for (int i = 0; i < 100 && client.get() < 0; ++i) {
        std::this_thread::sleep_for(20ms);
        client = connect_unix(socketPath.string());
    }
    ASSERT_GE(client.get(), 0) << "sentix-native did not start listening";
    EXPECT_EQ(permissions(socketPath), 0660U);

    const auto health = roundtrip(client.get(), envelope("HEALTH_CHECK"));
    ASSERT_FALSE(health.is_null());
    EXPECT_EQ(health.at("payload").at("status"), "READY");
    EXPECT_EQ(health.at("payload").at("modelLoaded"), true);

    ASSERT_EQ(::kill(pid, SIGTERM), 0);
    int status = 0;
    ASSERT_EQ(::waitpid(pid, &status, 0), pid);
    EXPECT_TRUE(WIFEXITED(status));
    EXPECT_EQ(WEXITSTATUS(status), 0);
    EXPECT_FALSE(std::filesystem::exists(socketPath));
}

TEST(SentixNativeBinary, StartsDegradedWithoutConfigAndFailsClosed) {
    sentix::testing::TempDir dir;
    const auto socketPath = dir.path() / "native.sock";

    const pid_t pid = ::fork();
    ASSERT_GE(pid, 0);
    if (pid == 0) {
        ::setenv("SENTIX_SOCKET_PATH", socketPath.c_str(), 1);
        ::setenv("SENTIX_CONFIG_PATH", (dir.path() / "missing.json").c_str(), 1);
        ::execl(SENTIX_NATIVE_BINARY, "sentix-native", static_cast<char*>(nullptr));
        ::_exit(127);
    }

    Fd client;
    for (int i = 0; i < 100 && client.get() < 0; ++i) {
        std::this_thread::sleep_for(20ms);
        client = connect_unix(socketPath.string());
    }
    ASSERT_GE(client.get(), 0);
    EXPECT_EQ(roundtrip(client.get(), envelope("HEALTH_CHECK")).at("payload").at("status"), "DEGRADED");
    EXPECT_EQ(roundtrip(client.get(), envelope("SCORE_EVENT", market_event("e1", "Apple beats record"))).at("payload").at("code"),
              "MODEL_NOT_READY");

    ASSERT_EQ(::kill(pid, SIGINT), 0);
    int status = 0;
    ASSERT_EQ(::waitpid(pid, &status, 0), pid);
    EXPECT_TRUE(WIFEXITED(status) && WEXITSTATUS(status) == 0);
    EXPECT_FALSE(std::filesystem::exists(socketPath));
}

namespace {

/// Starts the real daemon with a valid universe and the given engine environment, then asserts the
/// specs/002 §7 fail-closed contract: HEALTH_CHECK_ACK DEGRADED + modelLoaded=false, SCORE_EVENT -> MODEL_NOT_READY.
void expect_binary_fails_closed(const std::vector<std::pair<std::string, std::string>>& engineEnv) {
    sentix::testing::TempDir dir;
    const auto configPath = dir.write("universe.json", sentix::testing::read_file(sentix::testing::universe_json_path()));
    const auto socketPath = dir.path() / "native.sock";

    const pid_t pid = ::fork();
    ASSERT_GE(pid, 0);
    if (pid == 0) {
        ::setenv("SENTIX_SOCKET_PATH", socketPath.c_str(), 1);
        ::setenv("SENTIX_CONFIG_PATH", configPath.c_str(), 1);
        for (const auto& [name, value] : engineEnv) {
            ::setenv(name.c_str(), value.c_str(), 1);
        }
        ::execl(SENTIX_NATIVE_BINARY, "sentix-native", static_cast<char*>(nullptr));
        ::_exit(127);
    }

    Fd client;
    for (int i = 0; i < 100 && client.get() < 0; ++i) {
        std::this_thread::sleep_for(20ms);
        client = connect_unix(socketPath.string());
    }
    ASSERT_GE(client.get(), 0);
    const auto health = roundtrip(client.get(), envelope("HEALTH_CHECK"));
    ASSERT_FALSE(health.is_null());
    EXPECT_EQ(health.at("payload").at("status"), "DEGRADED");
    EXPECT_EQ(health.at("payload").at("modelLoaded"), false);
    const auto score = roundtrip(client.get(), envelope("SCORE_EVENT", market_event("e1", "Apple beats record", "", "", "ALPACA_BENZINGA",
                                                                                     "Benzinga", nlohmann::json::array({"AAPL"}))));
    ASSERT_FALSE(score.is_null());
    EXPECT_EQ(score.at("type"), "ERROR");
    EXPECT_EQ(score.at("payload").at("code"), "MODEL_NOT_READY");

    ASSERT_EQ(::kill(pid, SIGTERM), 0);
    int status = 0;
    ASSERT_EQ(::waitpid(pid, &status, 0), pid);
    EXPECT_TRUE(WIFEXITED(status) && WEXITSTATUS(status) == 0);
}

}  // namespace

TEST(SentixNativeBinary, OnnxModeWithMissingModelFailsClosed) {
    sentix::testing::TempDir models;
    expect_binary_fails_closed({{"SENTIX_ENGINE_MODE", "onnx"},
                                {"SENTIX_MODEL_PATH", (models.path() / "finbert-int8.onnx").string()},
                                {"SENTIX_VOCAB_PATH", (models.path() / "vocab.txt").string()}});
}

TEST(SentixNativeBinary, AutoModeWithCorruptModelFailsClosed) {
    sentix::testing::TempDir models;
    const auto model = models.write("finbert-int8.onnx", "corrupt");
    const auto vocab = models.write("vocab.txt", "[PAD]\n[UNK]\n[CLS]\n[SEP]\n");
    expect_binary_fails_closed({{"SENTIX_ENGINE_MODE", "auto"}, {"SENTIX_MODEL_PATH", model.string()}, {"SENTIX_VOCAB_PATH", vocab.string()}});
}

TEST(SentixNativeBinary, UnknownEngineModeFailsClosed) { expect_binary_fails_closed({{"SENTIX_ENGINE_MODE", "turbo"}}); }
