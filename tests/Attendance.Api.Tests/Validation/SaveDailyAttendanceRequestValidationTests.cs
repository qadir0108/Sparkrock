using System.ComponentModel.DataAnnotations;
using Attendance.Api.Dtos;
using Attendance.Api.Options;
using Attendance.Api.Tests.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Attendance.Api.Tests.Validation;

public sealed class SaveDailyAttendanceRequestValidationTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(TestData.Now.UtcDateTime);

    [Fact]
    public void EmptyEntries_IsRejected()
    {
        var request = Request(Today);

        var results = Validate(request);

        var error = Assert.Single(results);
        Assert.Contains(nameof(SaveDailyAttendanceRequest.Entries), error.MemberNames);
        Assert.Contains("At least one", error.ErrorMessage);
    }

    [Fact]
    public void DuplicateStudentIds_AreRejected_AndListed()
    {
        var request = Request(Today, (7, "P"), (3, "A"), (7, "A"), (3, "P"));

        var results = Validate(request);

        var error = Assert.Single(results);
        Assert.Contains(nameof(SaveDailyAttendanceRequest.Entries), error.MemberNames);
        Assert.Contains("3, 7", error.ErrorMessage);
    }

    [Fact]
    public void BatchLargerThanConfiguredMaximum_IsRejected()
    {
        var entries = Enumerable.Range(1, 4).Select(i => (i, "P")).ToArray();
        var request = Request(Today, entries);

        var results = Validate(request, new AttendanceOptions { MaxBatchSize = 3 });

        var error = Assert.Single(results);
        Assert.Contains("at most 3", error.ErrorMessage);
    }

    [Fact]
    public void DateBeyondAllowedFutureWindow_IsRejected()
    {
        var request = Request(Today.AddDays(2), (1, "P"));

        var results = Validate(request, new AttendanceOptions { MaxFutureDays = 1 });

        var error = Assert.Single(results);
        Assert.Contains(nameof(SaveDailyAttendanceRequest.AttendDate), error.MemberNames);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-30)]
    public void DateWithinAllowedWindow_IsAccepted(int daysFromToday)
    {
        var request = Request(Today.AddDays(daysFromToday), (1, "P"), (2, "A"));

        var results = Validate(request, new AttendanceOptions { MaxFutureDays = 1 });

        Assert.Empty(results);
    }

    [Fact]
    public void MissingDate_IsRejectedByRequiredAttribute()
    {
        var request = new SaveDailyAttendanceRequest
        {
            SchoolId = 1,
            AttendDate = null,
            Entries = [new DailyAttendanceEntryDto { StudentId = 1, AttendCode = "P" }],
        };

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(SaveDailyAttendanceRequest.AttendDate)));
    }

    [Fact]
    public void SchoolIdMustBePositive()
    {
        var request = new SaveDailyAttendanceRequest
        {
            SchoolId = 0,
            AttendDate = Today,
            Entries = [new DailyAttendanceEntryDto { StudentId = 1, AttendCode = "P" }],
        };

        var results = Validate(request);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(SaveDailyAttendanceRequest.SchoolId)));
    }

    [Theory]
    [InlineData(0, "P", 0, "StudentId")]
    [InlineData(1, "", 0, "AttendCode")]
    [InlineData(1, "TOOLONG", 0, "AttendCode")]
    [InlineData(1, "P", -1, "MinutesLate")]
    [InlineData(1, "P", 1441, "MinutesLate")]
    public void EntryAttributes_RejectBadValues(int studentId, string code, int minutesLate, string expectedMember)
    {
        var entry = new DailyAttendanceEntryDto { StudentId = studentId, AttendCode = code, MinutesLate = minutesLate };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(entry, new ValidationContext(entry), results, validateAllProperties: true);

        Assert.Contains(results, r => r.MemberNames.Contains(expectedMember));
    }

    [Fact]
    public void EntryNotesLongerThan500_AreRejected()
    {
        var entry = new DailyAttendanceEntryDto { StudentId = 1, AttendCode = "P", Notes = new string('x', 501) };
        var results = new List<ValidationResult>();

        Validator.TryValidateObject(entry, new ValidationContext(entry), results, validateAllProperties: true);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(DailyAttendanceEntryDto.Notes)));
    }

    private static SaveDailyAttendanceRequest Request(DateOnly date, params (int StudentId, string Code)[] entries) => new()
    {
        SchoolId = 1,
        AttendDate = date,
        Entries = entries.Select(e => new DailyAttendanceEntryDto { StudentId = e.StudentId, AttendCode = e.Code }).ToList(),
    };

    private static List<ValidationResult> Validate(SaveDailyAttendanceRequest request, AttendanceOptions? options = null)
    {
        var services = new ServiceCollection()
            .AddSingleton(Microsoft.Extensions.Options.Options.Create(options ?? new AttendanceOptions()))
            .AddSingleton<TimeProvider>(new FixedTimeProvider(TestData.Now))
            .BuildServiceProvider();

        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request, services, items: null), results, validateAllProperties: true);
        return results;
    }
}
