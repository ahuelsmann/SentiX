// SentiX native fast-path — rolling-window SHA-256 dedup (specs/002 §5 check 2).
#pragma once

#include <chrono>
#include <cstddef>
#include <deque>
#include <string>
#include <unordered_map>

namespace sentix::dedup {

using Clock = std::chrono::steady_clock;

/// Remembers dedup hashes for a rolling window. Not thread-safe (guarded by the request handler mutex).
class DedupCache {
public:
    explicit DedupCache(std::chrono::minutes window);

    /// Returns true when `hash` was recorded less than `window` ago (duplicate; first-seen time is kept).
    /// Otherwise records `hash` at `now` and returns false.
    [[nodiscard]] bool check_and_record(const std::string& hash, Clock::time_point now);

    void set_window(std::chrono::minutes window) noexcept { window_ = window; }
    [[nodiscard]] std::chrono::minutes window() const noexcept { return window_; }
    [[nodiscard]] std::size_t size() const noexcept { return seen_.size(); }

private:
    void evict_expired(Clock::time_point now);

    std::chrono::minutes window_;
    std::unordered_map<std::string, Clock::time_point> seen_;
    std::deque<std::pair<Clock::time_point, std::string>> order_;
};

}  // namespace sentix::dedup
