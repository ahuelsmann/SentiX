#include "sentix/wordpiece_tokenizer.hpp"

#include <fstream>
#include <ios>
#include <limits>
#include <system_error>
#include <utility>

namespace sentix::tokenizer {
namespace {

constexpr bool is_space(unsigned char c) noexcept {
    return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f';
}

constexpr bool is_control(unsigned char c) noexcept { return c < 0x20 || c == 0x7F; }

constexpr bool is_ascii_punctuation(unsigned char c) noexcept {
    return (c >= 33 && c <= 47) || (c >= 58 && c <= 64) || (c >= 91 && c <= 96) || (c >= 123 && c <= 126);
}

}  // namespace

WordPieceTokenizer::WordPieceTokenizer(std::vector<std::string> vocabulary) : idToToken_(std::move(vocabulary)) {
    if (idToToken_.empty()) {
        throw TokenizerError("vocab is empty");
    }
    tokenToId_.reserve(idToToken_.size());
    for (std::size_t i = 0; i < idToToken_.size(); ++i) {
        if (!idToToken_[i].empty()) {
            tokenToId_.emplace(idToToken_[i], static_cast<std::int64_t>(i));  // first occurrence wins
        }
    }
    unkId_ = require_special(kUnkToken);
    clsId_ = require_special(kClsToken);
    sepId_ = require_special(kSepToken);
    padId_ = require_special(kPadToken);
}

WordPieceTokenizer WordPieceTokenizer::from_vocab_text(std::string_view vocabText) {
    std::vector<std::string> vocabulary;
    std::size_t start = 0;
    while (start < vocabText.size()) {
        const std::size_t newline = vocabText.find('\n', start);
        std::string_view line =
            vocabText.substr(start, newline == std::string_view::npos ? std::string_view::npos : newline - start);
        if (!line.empty() && line.back() == '\r') {
            line.remove_suffix(1);
        }
        vocabulary.emplace_back(line);
        if (newline == std::string_view::npos) {
            break;
        }
        start = newline + 1;
    }
    return WordPieceTokenizer(std::move(vocabulary));
}

WordPieceTokenizer WordPieceTokenizer::from_file(const std::filesystem::path& vocabPath) {
    std::error_code ec;
    if (!std::filesystem::is_regular_file(vocabPath, ec)) {
        throw TokenizerError("vocab file '" + vocabPath.string() + "' does not exist or is not a regular file");
    }
    const auto size = std::filesystem::file_size(vocabPath, ec);
    if (ec) {
        throw TokenizerError("cannot stat vocab file '" + vocabPath.string() + "': " + ec.message());
    }
    if (size > kMaxVocabFileBytes) {
        throw TokenizerError("vocab file '" + vocabPath.string() + "' exceeds " + std::to_string(kMaxVocabFileBytes) + " bytes");
    }
    std::ifstream in(vocabPath, std::ios::binary);
    if (!in) {
        throw TokenizerError("cannot open vocab file '" + vocabPath.string() + "'");
    }
    std::string content(static_cast<std::size_t>(size), '\0');
    in.read(content.data(), static_cast<std::streamsize>(content.size()));
    if (in.gcount() != static_cast<std::streamsize>(content.size())) {
        throw TokenizerError("cannot read vocab file '" + vocabPath.string() + "'");
    }
    try {
        return from_vocab_text(content);
    } catch (const TokenizerError& ex) {
        throw TokenizerError("invalid vocab file '" + vocabPath.string() + "': " + ex.what());
    }
}

std::int64_t WordPieceTokenizer::require_special(std::string_view token) const {
    const auto it = tokenToId_.find(std::string(token));
    if (it == tokenToId_.end()) {
        throw TokenizerError("vocab is missing required special token " + std::string(token));
    }
    return it->second;
}

std::optional<std::int64_t> WordPieceTokenizer::token_id(std::string_view token) const {
    const auto it = tokenToId_.find(std::string(token));
    if (it == tokenToId_.end()) {
        return std::nullopt;
    }
    return it->second;
}

std::vector<std::string> WordPieceTokenizer::basic_tokenize(std::string_view text) const {
    std::vector<std::string> tokens;
    std::string current;
    const auto flush = [&tokens, &current] {
        if (!current.empty()) {
            tokens.push_back(std::move(current));
            current.clear();
        }
    };

    for (const char c : text) {
        const auto u = static_cast<unsigned char>(c);
        if (is_space(u) || is_control(u)) {
            flush();
        } else if (is_ascii_punctuation(u)) {
            flush();
            tokens.emplace_back(1, c);
        } else if (u >= 'A' && u <= 'Z') {
            current.push_back(static_cast<char>(u + ('a' - 'A')));
        } else {
            current.push_back(c);  // a-z, 0-9 and UTF-8 bytes stay part of the word
        }
    }
    flush();
    return tokens;
}

void WordPieceTokenizer::append_wordpiece_ids(std::string_view word, std::vector<std::int64_t>& out, std::size_t limit) const {
    if (word.empty() || out.size() >= limit) {
        return;
    }
    if (word.size() > kMaxInputCharsPerWord) {
        out.push_back(unkId_);
        return;
    }

    std::vector<std::int64_t> pieces;
    std::string candidate;
    std::size_t start = 0;
    while (start < word.size()) {
        std::size_t stop = word.size();
        std::optional<std::int64_t> match;
        while (start < stop) {
            candidate.assign(start > 0 ? "##" : "");
            candidate.append(word.substr(start, stop - start));
            if (const auto it = tokenToId_.find(candidate); it != tokenToId_.end()) {
                match = it->second;
                break;
            }
            --stop;
        }
        if (!match) {
            out.push_back(unkId_);  // unmatchable remainder -> the whole word becomes [UNK]
            return;
        }
        pieces.push_back(*match);
        start = stop;
    }

    for (const std::int64_t id : pieces) {
        if (out.size() >= limit) {
            break;
        }
        out.push_back(id);
    }
}

std::vector<std::string> WordPieceTokenizer::tokenize(std::string_view text) const {
    std::vector<std::int64_t> ids;
    for (const auto& word : basic_tokenize(text)) {
        append_wordpiece_ids(word, ids, std::numeric_limits<std::size_t>::max());
    }
    std::vector<std::string> tokens;
    tokens.reserve(ids.size());
    for (const std::int64_t id : ids) {
        tokens.push_back(idToToken_[static_cast<std::size_t>(id)]);
    }
    return tokens;
}

Encoding WordPieceTokenizer::encode(std::string_view text, std::size_t maxSequenceLength) const {
    if (maxSequenceLength < 2) {
        throw std::invalid_argument("maxSequenceLength must be >= 2 to hold [CLS] and [SEP]");
    }
    const std::size_t contentLimit = maxSequenceLength - 2;

    std::vector<std::int64_t> content;
    for (const auto& word : basic_tokenize(text)) {
        if (content.size() >= contentLimit) {
            break;
        }
        append_wordpiece_ids(word, content, contentLimit);
    }

    Encoding encoding;
    encoding.inputIds.reserve(content.size() + 2);
    encoding.inputIds.push_back(clsId_);
    encoding.inputIds.insert(encoding.inputIds.end(), content.begin(), content.end());
    encoding.inputIds.push_back(sepId_);
    encoding.attentionMask.assign(encoding.inputIds.size(), 1);
    encoding.tokenTypeIds.assign(encoding.inputIds.size(), 0);
    return encoding;
}

}  // namespace sentix::tokenizer
