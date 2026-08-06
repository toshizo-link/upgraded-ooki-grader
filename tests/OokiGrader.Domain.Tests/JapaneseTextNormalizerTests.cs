using OokiGrader.Domain.Grading;

namespace OokiGrader.Domain.Tests;

public sealed class JapaneseTextNormalizerTests
{
    [Fact]
    public void ComparisonNormalizationNormalizesWidthAndWhitespace()
    {
        var result = JapaneseTextNormalizer.NormalizeForComparison("　ＡＢＣ　１２３  ");

        Assert.Equal("ABC 123", result);
    }

    [Theory]
    [InlineData("かんじ", "かんじ")]
    [InlineData("カンジ", "カンジ")]
    [InlineData("漢字", "漢字")]
    public void ComparisonNormalizationPreservesJapaneseScript(string input, string expected)
    {
        Assert.Equal(expected, JapaneseTextNormalizer.NormalizeForComparison(input));
    }

    [Fact]
    public void ComparisonNormalizationDoesNotEquateHiraganaKatakanaOrKanji()
    {
        var hiragana = JapaneseTextNormalizer.NormalizeForComparison("かんじ");
        var katakana = JapaneseTextNormalizer.NormalizeForComparison("カンジ");
        var kanji = JapaneseTextNormalizer.NormalizeForComparison("漢字");

        Assert.NotEqual(hiragana, katakana);
        Assert.NotEqual(hiragana, kanji);
        Assert.NotEqual(katakana, kanji);
    }

    [Fact]
    public void ExactNormalizationDoesNotNormalizeWidth()
    {
        Assert.False(JapaneseTextNormalizer.ExactEquals("１２", "12"));
        Assert.True(JapaneseTextNormalizer.ComparisonEquals("１２", "12"));
    }

    [Theory]
    [InlineData("漢字", true)]
    [InlineData("人々", true)]
    [InlineData("〇", true)]
    [InlineData("かんじ", false)]
    [InlineData("カンジ", false)]
    [InlineData("", false)]
    public void KanjiDetectionRecognizesHanAndIterationMarks(
        string value,
        bool expected)
    {
        Assert.Equal(expected, KanjiDetector.ContainsKanji(value));
    }
}
