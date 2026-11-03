using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Attendance.Api.Services;

/// <summary>
/// The legacy school-year rule: a year starts on 1 September and is labelled "YYYY-YYYY+1"
/// (sp_SaveDailyAttendance.sql:32-35, sp_GetStudentAttendance.sql:10-13 and 30-34).
/// </summary>
public static partial class SchoolYearRule
{
    public const int StartMonth = 9;

    /// <summary>School-year label for a date, e.g. 2026-09-01 and 2027-06-15 both give "2026-2027".</summary>
    public static string For(DateOnly date) =>
        date.Month >= StartMonth
            ? $"{date.Year}-{date.Year + 1}"
            : $"{date.Year - 1}-{date.Year}";

    /// <summary>Half-open date range [start, endExclusive) covered by a school-year label.</summary>
    public static (DateOnly Start, DateOnly EndExclusive) Range(string schoolYear)
    {
        if (!TryParse(schoolYear, out var normalized))
        {
            throw new ArgumentException($"Value {schoolYear} is not a school year in the form YYYY-YYYY.", nameof(schoolYear));
        }

        var firstYear = int.Parse(normalized.AsSpan(0, 4));
        return (new DateOnly(firstYear, StartMonth, 1), new DateOnly(firstYear + 1, StartMonth, 1));
    }

    /// <summary>Accepts "YYYY-YYYY" where the second year is the first plus one.</summary>
    public static bool TryParse(string? value, [NotNullWhen(true)] out string? normalized)
    {
        normalized = null;
        if (value is null)
        {
            return false;
        }

        var match = Pattern().Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        var first = int.Parse(match.Groups[1].ValueSpan);
        var second = int.Parse(match.Groups[2].ValueSpan);
        if (second != first + 1 || first < 1900 || second > 9999)
        {
            return false;
        }

        normalized = $"{first}-{second}";
        return true;
    }

    [GeneratedRegex(@"^(\d{4})-(\d{4})$")]
    private static partial Regex Pattern();
}
