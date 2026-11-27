// SentiX native fast-path — BERT WordPiece tokenizer for ONNX-FinBERT (specs/002 §7).
//
// Pipeline: ASCII lower-case -> split on whitespace / control characters and isolate ASCII punctuation ->
// greedy longest-match-first WordPiece segmentation ("##" continuation pieces, [UNK] when unmatchable or a word
// exceeds 100 bytes) -> [CLS] tokens... [SEP] truncated to the maximum sequence length (default 512).
#pragma once

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <optional>
#include <stdexcept>
#include <string>
#include <string_view>
#include <unordered_map>
#include <vector>

namespace sentix::tokenizer {

inline constexpr std::size_t kMaxSequenceLength = 512;
inline constexpr std::size_t kMaxInputCharsPerWord = 100;
inline constexpr std::size_t kMaxVocabFileBytes = 64U * 1024U * 1024U;

inline constexpr std::string_view kUnkToken = "[UNK]";
inline constexpr std::string_view kClsToken = "[CLS]";
inline constexpr std::string_view kSepToken = "[SEP]";
inline constexpr std::string_view kPadToken = "[PAD]";

class TokenizerError : public std::runtime_error {
public:
    using std::runtime_error::runtime_error;
};

/// Model inputs for a single sequence (batch size 1, no padding). All three vectors have the same length.
struct Encoding {
    std::vector<std::int64_t> inputIds;
    std::vector<std::int64_t> attentionMask;  // all 1
    std::vector<std::int64_t> tokenTypeIds;   // all 0 (single segment)
};

class WordPieceTokenizer {
public:
    /// `vocabulary[i]` is the token with id i. Requires [UNK], [CLS], [SEP] and [PAD]. Throws TokenizerError.
    explicit WordPieceTokenizer(std::vector<std::string> vocabulary);

    /// Parses vocab.txt content: one token per line (LF or CRLF), 0-based line index == token id.
    [[nodiscard]] static WordPieceTokenizer from_vocab_text(std::string_view vocabText);

    /// Reads and parses a vocab.txt file. Throws TokenizerError when missing, unreadable, oversized or invalid.
    [[nodiscard]] static WordPieceTokenizer from_file(const std::filesystem::path& vocabPath);

    /// Lower-cased basic tokens (words and single punctuation characters) before WordPiece segmentation.
    [[nodiscard]] std::vector<std::string> basic_tokenize(std::string_view text) const;

    /// WordPiece token strings for `text` (without [CLS]/[SEP], untruncated).
    [[nodiscard]] std::vector<std::string> tokenize(std::string_view text) const;

    /// [CLS] + WordPiece ids + [SEP], truncated to `maxSequenceLength` (>= 2) total tokens.
    /// Throws std::invalid_argument when maxSequenceLength < 2.
    [[nodiscard]] Encoding encode(std::string_view text, std::size_t maxSequenceLength = kMaxSequenceLength) const;

    [[nodiscard]] std::optional<std::int64_t> token_id(std::string_view token) const;
    [[nodiscard]] std::size_t vocab_size() const noexcept { return idToToken_.size(); }
    [[nodiscard]] std::int64_t unk_id() const noexcept { return unkId_; }
    [[nodiscard]] std::int64_t cls_id() const noexcept { return clsId_; }
    [[nodiscard]] std::int64_t sep_id() const noexcept { return sepId_; }
    [[nodiscard]] std::int64_t pad_id() const noexcept { return padId_; }

private:
    /// Appends the WordPiece ids of one basic token; stops early once `out` holds `limit` ids.
    void append_wordpiece_ids(std::string_view word, std::vector<std::int64_t>& out, std::size_t limit) const;
    [[nodiscard]] std::int64_t require_special(std::string_view token) const;

    std::vector<std::string> idToToken_;
    std::unordered_map<std::string, std::int64_t> tokenToId_;
    std::int64_t unkId_{0};
    std::int64_t clsId_{0};
    std::int64_t sepId_{0};
    std::int64_t padId_{0};
};

}  // namespace sentix::tokenizer
