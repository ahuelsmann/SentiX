#include "sentix/hashing.hpp"

#include <array>
#include <stdexcept>

#include <openssl/evp.h>

#include "sentix/text_cleaner.hpp"

namespace sentix::hashing {

std::string sha256_hex(std::string_view data) {
    std::array<unsigned char, EVP_MAX_MD_SIZE> digest{};
    unsigned int digestLength = 0;
    if (EVP_Digest(data.data(), data.size(), digest.data(), &digestLength, EVP_sha256(), nullptr) != 1 || digestLength != 32U) {
        throw std::runtime_error("OpenSSL EVP_Digest(SHA-256) failed");
    }

    static constexpr char kHex[] = "0123456789abcdef";
    std::string hex;
    hex.reserve(static_cast<std::size_t>(digestLength) * 2U);
    for (unsigned int i = 0; i < digestLength; ++i) {
        hex.push_back(kHex[(digest[i] >> 4U) & 0x0FU]);
        hex.push_back(kHex[digest[i] & 0x0FU]);
    }
    return hex;
}

std::string dedup_hash(std::string_view effectiveText) {
    return "sha256:" + sha256_hex(text::normalize_for_dedup(effectiveText));
}

}  // namespace sentix::hashing
