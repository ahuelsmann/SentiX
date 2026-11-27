// SentiX native fast-path — text preprocessing (specs/002-ipc-contract.md §5 steps 1-2).
#pragma once

#include <string>
#include <string_view>

namespace sentix::text {

/// Removes HTML tags (`<...>`) and the contents of <script>/<style> elements. Tags are replaced by a single
/// space so adjacent block elements do not glue words together. A '<' that does not start a tag (e.g. "EPS < 1")
/// or that is never closed is preserved literally.
[[nodiscard]] std::string strip_tags(std::string_view input);

/// Decodes &amp; &lt; &gt; &quot; &#39; &apos; &nbsp; and numeric (&#NN; / &#xHH;) entities. Unknown entities stay literal.
[[nodiscard]] std::string decode_entities(std::string_view input);

/// Collapses runs of ASCII whitespace and UTF-8 NBSP (U+00A0) into a single space and trims both ends.
[[nodiscard]] std::string normalize_whitespace(std::string_view input);

/// strip_tags -> decode_entities -> normalize_whitespace.
[[nodiscard]] std::string clean(std::string_view input);

/// Summary fallback: trim(cleanHeadline + " " + (cleanBody.empty() ? cleanSummary : cleanBody)).
[[nodiscard]] std::string build_effective_text(std::string_view headline, std::string_view summary, std::string_view body);

/// Dedup normalization: ASCII lower-case + whitespace normalization.
[[nodiscard]] std::string normalize_for_dedup(std::string_view effectiveText);

/// ASCII lower-case copy.
[[nodiscard]] std::string to_lower_ascii(std::string_view input);

/// ASCII upper-case copy.
[[nodiscard]] std::string to_upper_ascii(std::string_view input);

/// Trims ASCII whitespace.
[[nodiscard]] std::string_view trim(std::string_view input) noexcept;

/// ASCII case-insensitive equality.
[[nodiscard]] bool iequals(std::string_view a, std::string_view b) noexcept;

}  // namespace sentix::text
