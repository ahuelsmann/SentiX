#include <gtest/gtest.h>

#include <regex>

#include "sentix/hashing.hpp"

using namespace sentix::hashing;

TEST(Hashing, Sha256KnownVectors) {
    EXPECT_EQ(sha256_hex(""), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    EXPECT_EQ(sha256_hex("abc"), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
}

TEST(Hashing, DedupHashFormatIsSha256PrefixPlus64LowercaseHex) {
    const std::string hash = dedup_hash("Apple beats guidance");
    EXPECT_TRUE(std::regex_match(hash, std::regex("^sha256:[0-9a-f]{64}$"))) << hash;
    EXPECT_EQ(hash, "sha256:" + sha256_hex("apple beats guidance"));
}

TEST(Hashing, DedupHashIgnoresCaseAndWhitespace) {
    EXPECT_EQ(dedup_hash("Apple  BEATS guidance"), dedup_hash(" apple beats\tGUIDANCE "));
    EXPECT_NE(dedup_hash("Apple beats guidance"), dedup_hash("Apple misses guidance"));
}
