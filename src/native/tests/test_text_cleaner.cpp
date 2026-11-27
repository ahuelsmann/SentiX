#include <gtest/gtest.h>

#include "sentix/text_cleaner.hpp"

using namespace sentix::text;

TEST(TextCleaner, StripsHtmlTagsAndKeepsWordBoundaries) {
    EXPECT_EQ(clean("<p>Apple announces</p><p>record demand</p>"), "Apple announces record demand");
    EXPECT_EQ(clean("<div class=\"x\"><b>Bold</b>&nbsp;<i>move</i></div>"), "Bold move");
    EXPECT_EQ(clean("line<br/>break<br>again"), "line break again");
    EXPECT_EQ(clean("<!-- comment -->text<?xml version=\"1.0\"?>"), "text");
}

TEST(TextCleaner, DropsScriptAndStyleContents) {
    EXPECT_EQ(clean("before<script type=\"text/javascript\">alert('x < y')</script>after"), "before after");
    EXPECT_EQ(clean("a<STYLE>p{color:red}</STYLE>b"), "a b");
    EXPECT_EQ(clean("a<script>never closed"), "a");
    EXPECT_EQ(clean("<scripted>keep</scripted>"), "keep");
}

TEST(TextCleaner, PreservesLiteralLessThanAndUnterminatedTags) {
    EXPECT_EQ(clean("EPS < 1.00 and revenue > guidance"), "EPS < 1.00 and revenue > guidance");
    EXPECT_EQ(clean("a <b unterminated"), "a <b unterminated");
    EXPECT_EQ(clean("trailing <"), "trailing <");
}

TEST(TextCleaner, DecodesRequiredEntities) {
    EXPECT_EQ(decode_entities("AT&amp;T &lt;tag&gt; &quot;q&quot; it&#39;s&nbsp;ok"), "AT&T <tag> \"q\" it's ok");
    EXPECT_EQ(decode_entities("&apos;x&apos;"), "'x'");
}

TEST(TextCleaner, DecodesNumericEntitiesAndKeepsUnknownOnes) {
    EXPECT_EQ(decode_entities("&#65;&#x42;&#X43;"), "ABC");
    EXPECT_EQ(decode_entities("&#8364;"), "\xE2\x82\xAC");      // euro sign, 3-byte UTF-8
    EXPECT_EQ(decode_entities("&#233;"), "\xC3\xA9");            // e acute, 2-byte UTF-8
    EXPECT_EQ(decode_entities("&#x1F600;"), "\xF0\x9F\x98\x80");  // emoji, 4-byte UTF-8
    EXPECT_EQ(decode_entities("&#160;"), " ");
    EXPECT_EQ(decode_entities("&copy; &unknown; & alone &#; &#x; &#xZZ; &#0; &#xD800; &#99999999;"),
              "&copy; &unknown; & alone &#; &#x; &#xZZ; &#0; &#xD800; &#99999999;");
}

TEST(TextCleaner, EntityDecodingHappensAfterTagStripping) {
    // Encoded markup must survive as literal text and is not re-interpreted as a tag.
    EXPECT_EQ(clean("&lt;b&gt;bold&lt;/b&gt;"), "<b>bold</b>");
}

TEST(TextCleaner, NormalizesWhitespace) {
    EXPECT_EQ(normalize_whitespace("  a \t\n b\r\n\fc\v  "), "a b c");
    EXPECT_EQ(normalize_whitespace("a\xC2\xA0\xC2\xA0" "b"), "a b");
    EXPECT_EQ(normalize_whitespace(" \n\t "), "");
    EXPECT_EQ(normalize_whitespace(""), "");
    EXPECT_EQ(normalize_whitespace("\xC2"), "\xC2");  // lone lead byte is not NBSP
}

TEST(TextCleaner, EffectiveTextUsesBodyWhenNonEmpty) {
    EXPECT_EQ(build_effective_text(" <b>Apple</b> beats ", "ignored summary", "<p>Record   quarter</p>"), "Apple beats Record quarter");
}

TEST(TextCleaner, SummaryFallbackWhenBodyIsEmptyAfterStripping) {
    EXPECT_EQ(build_effective_text("Apple beats", "Strong <i>iPhone</i> demand", ""), "Apple beats Strong iPhone demand");
    EXPECT_EQ(build_effective_text("Apple beats", "Strong demand", "<p></p><br/>"), "Apple beats Strong demand");
    EXPECT_EQ(build_effective_text("Apple beats", "Strong demand", " &nbsp; \n "), "Apple beats Strong demand");
    EXPECT_EQ(build_effective_text("Apple beats", "Strong demand", "<script>x()</script>"), "Apple beats Strong demand");
}

TEST(TextCleaner, EffectiveTextIsTrimmedWhenPartsAreEmpty) {
    EXPECT_EQ(build_effective_text("", "", ""), "");
    EXPECT_EQ(build_effective_text("Headline only", "", ""), "Headline only");
    EXPECT_EQ(build_effective_text("", "Summary only", ""), "Summary only");
    EXPECT_EQ(build_effective_text("", "", "Body only"), "Body only");
}

TEST(TextCleaner, DedupNormalizationIsCaseAndWhitespaceInsensitive) {
    EXPECT_EQ(normalize_for_dedup("  Apple   BEATS\tGuidance "), "apple beats guidance");
}

TEST(TextCleaner, AsciiHelpers) {
    EXPECT_EQ(to_lower_ascii("AbC-1"), "abc-1");
    EXPECT_EQ(to_upper_ascii("aBc-1"), "ABC-1");
    EXPECT_EQ(trim("  x y \n"), "x y");
    EXPECT_EQ(trim("   "), "");
    EXPECT_TRUE(iequals("WhiteHouse", "whitehouse"));
    EXPECT_FALSE(iequals("WhiteHouse", "whitehous"));
    EXPECT_FALSE(iequals("abc", "abd"));
}
