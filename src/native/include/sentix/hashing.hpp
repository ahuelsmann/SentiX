// SentiX native fast-path — SHA-256 dedup keys (OpenSSL EVP).
#pragma once

#include <string>
#include <string_view>

namespace sentix::hashing {

/// SHA-256 of `data` as 64 lowercase hex characters. Throws std::runtime_error on OpenSSL failure.
[[nodiscard]] std::string sha256_hex(std::string_view data);

/// `"sha256:" + sha256_hex(text::normalize_for_dedup(effectiveText))`.
[[nodiscard]] std::string dedup_hash(std::string_view effectiveText);

}  // namespace sentix::hashing
