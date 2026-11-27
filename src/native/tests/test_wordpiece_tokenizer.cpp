#include <gtest/gtest.h>

#include <cstdint>
#include <stdexcept>
#include <string>
#include <vector>

#include "sentix/wordpiece_tokenizer.hpp"
#include "test_helpers.hpp"

using sentix::tokenizer::TokenizerError;
using sentix::tokenizer::WordPieceTokenizer;
using Strings = std::vector<std::string>;
using Ids = std::vector<std::int64_t>;

namespace {

// id:                 0        1        2        3        4         5        6       7      8         9
constexpr const char* kVocab = "[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\napple\nbeat\n##s\nrecord\ndemand\n"
                               // 10  11  12    13        14      15      16      17  18      19
                               ",\n.\nun\n##want\n##ed\nrunn\n##ing\n$\naapl\n!\n";

WordPieceTokenizer make_tokenizer() { return WordPieceTokenizer::from_vocab_text(kVocab); }

}  // namespace

TEST(WordPieceTokenizer, LoadsVocabWithZeroBasedIdsAndSpecialTokens) {
    const auto tok = make_tokenizer();
    EXPECT_EQ(tok.vocab_size(), 20U);
    EXPECT_EQ(tok.pad_id(), 0);
    EXPECT_EQ(tok.unk_id(), 1);
    EXPECT_EQ(tok.cls_id(), 2);
    EXPECT_EQ(tok.sep_id(), 3);
    EXPECT_EQ(tok.token_id("apple"), 5);
    EXPECT_EQ(tok.token_id("##s"), 7);
    EXPECT_FALSE(tok.token_id("banana").has_value());
}

TEST(WordPieceTokenizer, AcceptsCrLfAndMissingTrailingNewline) {
    const auto tok = WordPieceTokenizer::from_vocab_text("[PAD]\r\n[UNK]\r\n[CLS]\r\n[SEP]\r\nhello");
    EXPECT_EQ(tok.vocab_size(), 5U);
    EXPECT_EQ(tok.token_id("hello"), 4);
    EXPECT_FALSE(tok.token_id("hello\r").has_value());
}

TEST(WordPieceTokenizer, RequiresSpecialTokens) {
    EXPECT_THROW(static_cast<void>(WordPieceTokenizer::from_vocab_text("")), TokenizerError);
    EXPECT_THROW(static_cast<void>(WordPieceTokenizer::from_vocab_text("[PAD]\n[CLS]\n[SEP]\n")), TokenizerError);  // no [UNK]
    EXPECT_THROW(static_cast<void>(WordPieceTokenizer::from_vocab_text("[PAD]\n[UNK]\n[SEP]\n")), TokenizerError);  // no [CLS]
    EXPECT_THROW(static_cast<void>(WordPieceTokenizer::from_vocab_text("[PAD]\n[UNK]\n[CLS]\n")), TokenizerError);  // no [SEP]
    EXPECT_THROW(static_cast<void>(WordPieceTokenizer::from_vocab_text("[UNK]\n[CLS]\n[SEP]\n")), TokenizerError);  // no [PAD]
    EXPECT_THROW(static_cast<void>(WordPieceTokenizer(Strings{})), TokenizerError);
}

TEST(WordPieceTokenizer, DuplicateTokensKeepFirstId) {
    const auto tok = WordPieceTokenizer::from_vocab_text("[PAD]\n[UNK]\n[CLS]\n[SEP]\nx\nx\n");
    EXPECT_EQ(tok.token_id("x"), 4);
    EXPECT_EQ(tok.vocab_size(), 6U);
}

TEST(WordPieceTokenizer, LoadsFromFile) {
    sentix::testing::TempDir dir;
    const auto path = dir.write("vocab.txt", kVocab);
    const auto tok = WordPieceTokenizer::from_file(path);
    EXPECT_EQ(tok.vocab_size(), 20U);
    EXPECT_EQ(tok.token_id("aapl"), 18);
}

TEST(WordPieceTokenizer, FromFileFailsForMissingOrInvalidFiles) {
    sentix::testing::TempDir dir;
    EXPECT_THROW(static_cast<void>(WordPieceTokenizer::from_file(dir.path() / "missing.txt")), TokenizerError);
    EXPECT_THROW(static_cast<void>(WordPieceTokenizer::from_file(dir.path())), TokenizerError);  // directory
    const auto corrupt = dir.write("corrupt.txt", std::string("\x7f" "ELF\x02\x01\x01\0\0\0", 10));
    EXPECT_THROW(static_cast<void>(WordPieceTokenizer::from_file(corrupt)), TokenizerError);
}

TEST(WordPieceTokenizer, BasicTokenizeLowercasesAndSplitsPunctuation) {
    const auto tok = make_tokenizer();
    EXPECT_EQ(tok.basic_tokenize("Apple's $AAPL beats, record-high!"),
              (Strings{"apple", "'", "s", "$", "aapl", "beats", ",", "record", "-", "high", "!"}));
    EXPECT_EQ(tok.basic_tokenize("  \t\n  "), Strings{});
    EXPECT_EQ(tok.basic_tokenize("a\x01" "b\x7f" "c"), (Strings{"a", "b", "c"}));  // control characters split
    EXPECT_EQ(tok.basic_tokenize("caf\xc3\xa9 OK"), (Strings{"caf\xc3\xa9", "ok"}));  // UTF-8 bytes stay in the word
}

TEST(WordPieceTokenizer, GreedyLongestMatchFirstSubwords) {
    const auto tok = make_tokenizer();
    EXPECT_EQ(tok.tokenize("Apple beats record demand, UNWANTED running!"),
              (Strings{"apple", "beat", "##s", "record", "demand", ",", "un", "##want", "##ed", "runn", "##ing", "!"}));
}

TEST(WordPieceTokenizer, UnmatchableWordsBecomeSingleUnk) {
    const auto tok = make_tokenizer();
    // "beatx": "beat" matches but "##x" does not -> the whole word is [UNK] (no partial pieces).
    EXPECT_EQ(tok.tokenize("beatx zzz apple"), (Strings{"[UNK]", "[UNK]", "apple"}));
}

TEST(WordPieceTokenizer, OverlongWordsBecomeUnk) {
    const auto tok = make_tokenizer();
    std::string longWord;
    for (int i = 0; i < 26; ++i) {
        longWord += "beat";  // 104 chars, would otherwise be segmentable
    }
    EXPECT_EQ(tok.tokenize(longWord), Strings{"[UNK]"});
    EXPECT_EQ(tok.tokenize(std::string(100, 'z')), Strings{"[UNK]"});
}

TEST(WordPieceTokenizer, EncodeWrapsWithClsSepAndBuildsMasks) {
    const auto tok = make_tokenizer();
    const auto enc = tok.encode("Apple beats");
    EXPECT_EQ(enc.inputIds, (Ids{2, 5, 6, 7, 3}));
    EXPECT_EQ(enc.attentionMask, (Ids{1, 1, 1, 1, 1}));
    EXPECT_EQ(enc.tokenTypeIds, (Ids{0, 0, 0, 0, 0}));

    const auto empty = tok.encode("");
    EXPECT_EQ(empty.inputIds, (Ids{2, 3}));
    EXPECT_EQ(empty.attentionMask, (Ids{1, 1}));
    EXPECT_EQ(empty.tokenTypeIds, (Ids{0, 0}));
}

TEST(WordPieceTokenizer, EncodeTruncatesToMaxSequenceLength) {
    const auto tok = make_tokenizer();
    EXPECT_EQ(tok.encode("Apple beats record demand", 4).inputIds, (Ids{2, 5, 6, 3}));  // truncation inside "beats"
    EXPECT_EQ(tok.encode("Apple beats record demand", 2).inputIds, (Ids{2, 3}));
    EXPECT_THROW(static_cast<void>(tok.encode("Apple", 1)), std::invalid_argument);
    EXPECT_THROW(static_cast<void>(tok.encode("Apple", 0)), std::invalid_argument);
}

TEST(WordPieceTokenizer, EncodeCapsAtDefault512Tokens) {
    const auto tok = make_tokenizer();
    std::string text;
    for (int i = 0; i < 2000; ++i) {
        text += "apple beats ";
    }
    const auto enc = tok.encode(text);
    ASSERT_EQ(enc.inputIds.size(), sentix::tokenizer::kMaxSequenceLength);
    EXPECT_EQ(enc.inputIds.front(), tok.cls_id());
    EXPECT_EQ(enc.inputIds.back(), tok.sep_id());
    EXPECT_EQ(enc.attentionMask.size(), 512U);
    EXPECT_EQ(enc.tokenTypeIds.size(), 512U);
}
