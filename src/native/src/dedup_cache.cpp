#include "sentix/dedup_cache.hpp"

namespace sentix::dedup {

DedupCache::DedupCache(std::chrono::minutes window) : window_(window) {}

bool DedupCache::check_and_record(const std::string& hash, Clock::time_point now) {
    evict_expired(now);
    if (const auto it = seen_.find(hash); it != seen_.end()) {
        if (now - it->second < window_) {
            return true;
        }
        seen_.erase(it);  // stale entry still present (window shrank): treat as new
    }
    seen_.emplace(hash, now);
    order_.emplace_back(now, hash);
    return false;
}

void DedupCache::evict_expired(Clock::time_point now) {
    while (!order_.empty() && now - order_.front().first >= window_) {
        const auto& [recordedAt, hash] = order_.front();
        // Only erase when the map still holds this exact recording (a re-recorded hash has a newer time).
        if (const auto it = seen_.find(hash); it != seen_.end() && it->second == recordedAt) {
            seen_.erase(it);
        }
        order_.pop_front();
    }
}

}  // namespace sentix::dedup
