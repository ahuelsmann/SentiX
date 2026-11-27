#include "sentix/text_cleaner.hpp"

#include <array>
#include <cstdint>
#include <optional>
#include <utility>

namespace sentix::text {
namespace {

constexpr bool is_ascii_space(char c) noexcept {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v';
}

constexpr bool is_ascii_alpha(char c) noexcept { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }

constexpr char lower(char c) noexcept { return (c >= 'A' && c <= 'Z') ? static_cast<char>(c - 'A' + 'a') : c; }

constexpr char upper(char c) noexcept { return (c >= 'a' && c <= 'z') ? static_cast<char>(c - 'a' + 'A') : c; }

/// '<' starts a tag only when followed by a letter, '/', '!' or '?' (so "EPS < 1" stays literal).
bool starts_tag(std::string_view s, std::size_t i) noexcept {
    if (i + 1 >= s.size()) {
        return false;
    }
    const char next = s[i + 1];
    return is_ascii_alpha(next) || next == '/' || next == '!' || next == '?';
}

/// Case-insensitive search for `needle` starting at `from`.
std::size_t ifind(std::string_view haystack, std::string_view needle, std::size_t from) noexcept {
    if (needle.size() > haystack.size()) {
        return std::string_view::npos;
    }
    for (std::size_t i = from; i + needle.size() <= haystack.size(); ++i) {
        if (iequals(haystack.substr(i, needle.size()), needle)) {
            return i;
        }
    }
    return std::string_view::npos;
}

void append_utf8(std::string& out, std::uint32_t cp) {
    if (cp < 0x80U) {
        out.push_back(static_cast<char>(cp));
    } else if (cp < 0x800U) {
        out.push_back(static_cast<char>(0xC0U | (cp >> 6U)));
        out.push_back(static_cast<char>(0x80U | (cp & 0x3FU)));
    } else if (cp < 0x10000U) {
        out.push_back(static_cast<char>(0xE0U | (cp >> 12U)));
        out.push_back(static_cast<char>(0x80U | ((cp >> 6U) & 0x3FU)));
        out.push_back(static_cast<char>(0x80U | (cp & 0x3FU)));
    } else {
        out.push_back(static_cast<char>(0xF0U | (cp >> 18U)));
        out.push_back(static_cast<char>(0x80U | ((cp >> 12U) & 0x3FU)));
        out.push_back(static_cast<char>(0x80U | ((cp >> 6U) & 0x3FU)));
        out.push_back(static_cast<char>(0x80U | (cp & 0x3FU)));
    }
}

std::optional<std::uint32_t> parse_numeric_entity(std::string_view digits) noexcept {
    // digits excludes "&#" and ';'
    if (digits.empty() || digits.size() > 8) {
        return std::nullopt;
    }
    std::uint32_t base = 10;
    if (digits[0] == 'x' || digits[0] == 'X') {
        base = 16;
        digits.remove_prefix(1);
        if (digits.empty()) {
            return std::nullopt;
        }
    }
    std::uint32_t value = 0;
    for (const char c : digits) {
        std::uint32_t d = 0;
        if (c >= '0' && c <= '9') {
            d = static_cast<std::uint32_t>(c - '0');
        } else if (base == 16 && c >= 'a' && c <= 'f') {
            d = static_cast<std::uint32_t>(c - 'a' + 10);
        } else if (base == 16 && c >= 'A' && c <= 'F') {
            d = static_cast<std::uint32_t>(c - 'A' + 10);
        } else {
            return std::nullopt;
        }
        value = value * base + d;
    }
    // Reject NUL, surrogates and out-of-range code points.
    if (value == 0U || value > 0x10FFFFU || (value >= 0xD800U && value <= 0xDFFFU)) {
        return std::nullopt;
    }
    return value;
}

}  // namespace

std::string strip_tags(std::string_view input) {
    std::string out;
    out.reserve(input.size());
    std::size_t i = 0;
    while (i < input.size()) {
        if (input[i] != '<' || !starts_tag(input, i)) {
            out.push_back(input[i]);
            ++i;
            continue;
        }

        const std::size_t close = input.find('>', i + 1);
        if (close == std::string_view::npos) {
            // Unterminated tag: keep the remainder literally (no silent data loss).
            out.append(input.substr(i));
            break;
        }

        // Drop the contents of <script> and <style> elements entirely.
        const std::string_view tag = input.substr(i + 1, close - i - 1);
        bool skipped = false;
        for (const std::string_view element : {std::string_view{"script"}, std::string_view{"style"}}) {
            if (tag.size() >= element.size() && iequals(tag.substr(0, element.size()), element) &&
                (tag.size() == element.size() || !is_ascii_alpha(tag[element.size()]))) {
                const std::string closing = "</" + std::string(element);
                const std::size_t end = ifind(input, closing, close + 1);
                const std::size_t endClose = end == std::string_view::npos ? std::string_view::npos : input.find('>', end);
                i = endClose == std::string_view::npos ? input.size() : endClose + 1;
                skipped = true;
                break;
            }
        }
        if (!skipped) {
            i = close + 1;
        }
        out.push_back(' ');
    }
    return out;
}

std::string decode_entities(std::string_view input) {
    static constexpr std::array<std::pair<std::string_view, std::string_view>, 7> kNamed{{
        {"&amp;", "&"},
        {"&lt;", "<"},
        {"&gt;", ">"},
        {"&quot;", "\""},
        {"&#39;", "'"},
        {"&apos;", "'"},
        {"&nbsp;", " "},
    }};

    std::string out;
    out.reserve(input.size());
    std::size_t i = 0;
    while (i < input.size()) {
        if (input[i] != '&') {
            out.push_back(input[i]);
            ++i;
            continue;
        }

        bool decoded = false;
        for (const auto& [entity, replacement] : kNamed) {
            if (input.substr(i, entity.size()) == entity) {
                out.append(replacement);
                i += entity.size();
                decoded = true;
                break;
            }
        }
        if (decoded) {
            continue;
        }

        if (i + 2 < input.size() && input[i + 1] == '#') {
            const std::size_t semi = input.find(';', i + 2);
            if (semi != std::string_view::npos) {
                if (const auto cp = parse_numeric_entity(input.substr(i + 2, semi - i - 2))) {
                    append_utf8(out, *cp == 0xA0U ? 0x20U : *cp);
                    i = semi + 1;
                    continue;
                }
            }
        }

        out.push_back('&');
        ++i;
    }
    return out;
}

std::string normalize_whitespace(std::string_view input) {
    std::string out;
    out.reserve(input.size());
    bool pendingSpace = false;
    std::size_t i = 0;
    while (i < input.size()) {
        const char c = input[i];
        const bool nbsp = static_cast<unsigned char>(c) == 0xC2U && i + 1 < input.size() &&
                          static_cast<unsigned char>(input[i + 1]) == 0xA0U;
        if (is_ascii_space(c) || nbsp) {
            pendingSpace = !out.empty();
            i += nbsp ? 2 : 1;
            continue;
        }
        if (pendingSpace) {
            out.push_back(' ');
            pendingSpace = false;
        }
        out.push_back(c);
        ++i;
    }
    return out;
}

std::string clean(std::string_view input) { return normalize_whitespace(decode_entities(strip_tags(input))); }

std::string build_effective_text(std::string_view headline, std::string_view summary, std::string_view body) {
    const std::string cleanHeadline = clean(headline);
    const std::string cleanBody = clean(body);
    const std::string detail = cleanBody.empty() ? clean(summary) : cleanBody;
    return normalize_whitespace(cleanHeadline + " " + detail);
}

std::string normalize_for_dedup(std::string_view effectiveText) { return to_lower_ascii(normalize_whitespace(effectiveText)); }

std::string to_lower_ascii(std::string_view input) {
    std::string out(input);
    for (char& c : out) {
        c = lower(c);
    }
    return out;
}

std::string to_upper_ascii(std::string_view input) {
    std::string out(input);
    for (char& c : out) {
        c = upper(c);
    }
    return out;
}

std::string_view trim(std::string_view input) noexcept {
    while (!input.empty() && is_ascii_space(input.front())) {
        input.remove_prefix(1);
    }
    while (!input.empty() && is_ascii_space(input.back())) {
        input.remove_suffix(1);
    }
    return input;
}

bool iequals(std::string_view a, std::string_view b) noexcept {
    if (a.size() != b.size()) {
        return false;
    }
    for (std::size_t i = 0; i < a.size(); ++i) {
        if (lower(a[i]) != lower(b[i])) {
            return false;
        }
    }
    return true;
}

}  // namespace sentix::text
