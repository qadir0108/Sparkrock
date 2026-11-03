using Attendance.Api.Services;

namespace Attendance.Api.Tests.Services;

public sealed class SchoolYearRuleTests
{
    [Theory]
    [InlineData(2026, 9, 1, "2026-2027")]
    [InlineData(2026, 12, 31, "2026-2027")]
    [InlineData(2027, 1, 1, "2026-2027")]
    [InlineData(2027, 8, 31, "2026-2027")]
    [InlineData(2026, 8, 31, "2025-2026")]
    public void For_UsesSeptemberBoundary(int year, int month, int day, string expected)
    {
        Assert.Equal(expected, SchoolYearRule.For(new DateOnly(year, month, day)));
    }

    [Fact]
    public void Range_CoversFirstSeptemberToNextFirstSeptemberExclusive()
    {
        var (start, endExclusive) = SchoolYearRule.Range("2026-2027");

        Assert.Equal(new DateOnly(2026, 9, 1), start);
        Assert.Equal(new DateOnly(2027, 9, 1), endExclusive);
    }

    [Fact]
    public void Range_RejectsMalformedLabel()
    {
        Assert.Throws<ArgumentException>(() => SchoolYearRule.Range("2026-2028"));
    }

    [Theory]
    [InlineData("2026-2027", true, "2026-2027")]
    [InlineData("  2026-2027 ", true, "2026-2027")]
    [InlineData("2026-2028", false, null)]
    [InlineData("2026", false, null)]
    [InlineData(null, false, null)]
    public void TryParse_AcceptsOnlyConsecutiveYears(string? input, bool expectedOk, string? expectedNormalized)
    {
        var ok = SchoolYearRule.TryParse(input, out var normalized);

        Assert.Equal(expectedOk, ok);
        Assert.Equal(expectedNormalized, normalized);
    }
}
