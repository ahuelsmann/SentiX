#include <gtest/gtest.h>

#include "sentix/dedup_cache.hpp"

using namespace std::chrono_literals;
using sentix::dedup::Clock;
using sentix::dedup::DedupCache;

namespace {
const Clock::time_point t0 = Clock::time_point{} + 1000h;
}

TEST(DedupCache, FirstSeenIsNotDuplicate_SecondWithinWindowIs) {
    DedupCache cache(180min);
    EXPECT_FALSE(cache.check_and_record("sha256:a", t0));
    EXPECT_TRUE(cache.check_and_record("sha256:a", t0 + 1min));
    EXPECT_TRUE(cache.check_and_record("sha256:a", t0 + 179min + 59s));
    EXPECT_FALSE(cache.check_and_record("sha256:b", t0 + 1min));
}

TEST(DedupCache, WindowBoundaryIsExclusive) {
    DedupCache cache(180min);
    EXPECT_FALSE(cache.check_and_record("sha256:a", t0));
    EXPECT_FALSE(cache.check_and_record("sha256:a", t0 + 180min));  // exactly 180 min later -> new
    EXPECT_TRUE(cache.check_and_record("sha256:a", t0 + 200min));   // re-recorded at +180
}

TEST(DedupCache, DuplicatesDoNotExtendTheWindow) {
    DedupCache cache(10min);
    EXPECT_FALSE(cache.check_and_record("h", t0));
    EXPECT_TRUE(cache.check_and_record("h", t0 + 9min));
    EXPECT_FALSE(cache.check_and_record("h", t0 + 10min));  // window counts from first sighting
}

TEST(DedupCache, ExpiredEntriesAreEvicted) {
    DedupCache cache(10min);
    for (int i = 0; i < 100; ++i) {
        EXPECT_FALSE(cache.check_and_record("h" + std::to_string(i), t0));
    }
    EXPECT_EQ(cache.size(), 100U);
    EXPECT_FALSE(cache.check_and_record("fresh", t0 + 11min));
    EXPECT_EQ(cache.size(), 1U);
}

TEST(DedupCache, WindowCanBeChangedAtRuntime) {
    DedupCache cache(180min);
    EXPECT_EQ(cache.window(), 180min);
    EXPECT_FALSE(cache.check_and_record("h", t0));
    cache.set_window(5min);
    EXPECT_FALSE(cache.check_and_record("h", t0 + 6min));
    cache.set_window(60min);
    EXPECT_TRUE(cache.check_and_record("h", t0 + 30min));
}
