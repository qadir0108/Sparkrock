using System.ComponentModel.DataAnnotations;
using Attendance.Api.Dtos;

namespace Attendance.Api.Tests.Validation;

public sealed class QueryDtoValidationTests
{
    [Fact]
    public void HistoryQuery_FromAfterTo_IsRejected()
    {
        var query = new AttendanceHistoryQuery { From = new DateOnly(2026, 10, 2), To = new DateOnly(2026, 10, 1) };

        var results = Validate(query);

        var error = Assert.Single(results);
        Assert.Contains(nameof(AttendanceHistoryQuery.From), error.MemberNames);
        Assert.Contains(nameof(AttendanceHistoryQuery.To), error.MemberNames);
    }

    [Fact]
    public void HistoryQuery_FromEqualToTo_IsAccepted()
    {
        var query = new AttendanceHistoryQuery { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 1) };

        Assert.Empty(Validate(query));
    }

    [Fact]
    public void HistoryQuery_OpenEndedRanges_AreAccepted()
    {
        Assert.Empty(Validate(new AttendanceHistoryQuery()));
        Assert.Empty(Validate(new AttendanceHistoryQuery { From = new DateOnly(2026, 1, 1) }));
        Assert.Empty(Validate(new AttendanceHistoryQuery { To = new DateOnly(2026, 1, 1) }));
    }

    [Theory]
    [InlineData("2026")]
    [InlineData("2026-2028")]
    [InlineData("2027-2026")]
    [InlineData("26-27")]
    [InlineData("abcd-efgh")]
    public void AbsenteeismQuery_MalformedSchoolYear_IsRejected(string schoolYear)
    {
        var results = Validate(new AbsenteeismQuery { SchoolYear = schoolYear });

        var error = Assert.Single(results);
        Assert.Contains(nameof(AbsenteeismQuery.SchoolYear), error.MemberNames);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2026-2027")]
    [InlineData(" 2025-2026 ")]
    public void AbsenteeismQuery_ValidOrAbsentSchoolYear_IsAccepted(string? schoolYear)
    {
        Assert.Empty(Validate(new AbsenteeismQuery { SchoolYear = schoolYear }));
    }

    private static List<ValidationResult> Validate(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }
}
