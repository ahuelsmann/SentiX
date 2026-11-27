// sentix-native — SentiX C++20 fast-path daemon (specs/002-ipc-contract.md).
//
// Environment:
//   SENTIX_SOCKET_PATH  Unix domain socket path   (default /run/sentix/native.sock)
//   SENTIX_CONFIG_PATH  universe.json path        (default /config/universe.json)
//   SENTIX_ONNX_MODEL   reserved for Phase 2 ONNX-FinBERT (ignored by the Phase-1 mock engine)

#include <atomic>
#include <csignal>
#include <cstdlib>
#include <cstring>
#include <exception>
#include <iostream>
#include <memory>
#include <string>

#include "sentix/config.hpp"
#include "sentix/protocol.hpp"
#include "sentix/sentiment_engine.hpp"
#include "sentix/unix_socket_server.hpp"

namespace {

std::atomic<sentix::server::UnixSocketServer*> g_server{nullptr};

extern "C" void on_shutdown_signal(int /*signal*/) {
    if (auto* server = g_server.load()) {
        server->request_stop_from_signal();  // async-signal-safe: write(2) only
    }
}

std::string env_or(const char* name, std::string_view fallback) {
    const char* value = std::getenv(name);
    return (value != nullptr && *value != '\0') ? std::string(value) : std::string(fallback);
}

void install_signal_handlers() {
    struct sigaction action{};
    action.sa_handler = on_shutdown_signal;
    sigemptyset(&action.sa_mask);
    action.sa_flags = 0;  // no SA_RESTART: let poll() return EINTR promptly
    sigaction(SIGINT, &action, nullptr);
    sigaction(SIGTERM, &action, nullptr);

    struct sigaction ignore{};
    ignore.sa_handler = SIG_IGN;
    sigemptyset(&ignore.sa_mask);
    sigaction(SIGPIPE, &ignore, nullptr);
}

void log(std::string_view level, std::string_view message) {
    std::cerr << sentix::protocol::format_utc(std::chrono::system_clock::now()) << " [" << level << "] sentix-native: " << message
              << std::endl;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc > 1 && (std::strcmp(argv[1], "--version") == 0 || std::strcmp(argv[1], "-v") == 0)) {
        std::cout << "sentix-native " << sentix::protocol::kEngineVersion << '\n';
        return EXIT_SUCCESS;
    }

    const std::string socketPath = env_or("SENTIX_SOCKET_PATH", sentix::server::kDefaultSocketPath);
    const std::string configPath = env_or("SENTIX_CONFIG_PATH", sentix::config::kDefaultConfigPath);

    install_signal_handlers();

    try {
        sentix::protocol::RequestHandler handler(configPath, std::make_unique<sentix::sentiment::MockSentimentEngine>());
        if (handler.load_initial_config()) {
            log("INFO", "universe loaded from " + configPath + " - status READY");
        } else {
            log("WARN", "universe config unavailable at " + configPath + " - status DEGRADED (fail-closed, awaiting RELOAD_CONFIG)");
        }

        sentix::server::UnixSocketServer server(
            socketPath, [&handler](std::string_view request) { return handler.handle(request); },
            [&handler](std::string_view code, std::string_view message) { return handler.frame_error(code, message); });
        server.start();
        g_server.store(&server);
        log("INFO", "listening on " + socketPath + " (engine " + std::string(sentix::protocol::kEngineVersion) + ")");

        server.run();
        g_server.store(nullptr);
        log("INFO", "shutdown complete, socket unlinked");
        return EXIT_SUCCESS;
    } catch (const std::exception& ex) {
        g_server.store(nullptr);
        log("FATAL", ex.what());
        return EXIT_FAILURE;
    }
}
