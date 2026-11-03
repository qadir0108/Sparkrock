using Attendance.Api.Data;
using Attendance.Api.Entities;
using Attendance.Api.Options;
using Attendance.Api.Services;
using Attendance.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace Attendance.Api.Tests.Services;

public sealed class StudentAttendanceServiceTests : IDisposable
{
    private readonly SqliteTestDatabase _database = new();

    public StudentAttendanceServiceTests()
    {
        using var db = _database.CreateContext();
        TestData.SeedAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task GetHistoryAsync_ThrowsNotFound_ForUnknownStudent()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => service.GetHistoryAsync(999, null, null, CancellationToken.None));

        Assert.Contains("Student 999", ex.Message);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsEmptyList_WhenStudentHasNoRecords()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var records = await service.GetHistoryAsync(TestData.Ada, null, null, CancellationToken.None);

        Assert.Empty(records);
    }

    [Fact]
    public async Task GetHistoryAsync_ThrowsValidation_WhenFromIsAfterTo()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<AttendanceValidationException>(() =>
            service.GetHistoryAsync(TestData.Ada, new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1), CancellationToken.None));

        Assert.True(ex.Errors.ContainsKey("to"));
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsNewestFirst_WithDescriptionAndTermName_AndHonoursRange()
    {
        await SeedAttendanceAsync(TestData.Ada,
            (new DateOnly(2026, 8, 20), "P", null),
            (new DateOnly(2026, 9, 10), "P", TestData.FallTermId),
            (new DateOnly(2026, 9, 11), "A", TestData.FallTermId),
            (new DateOnly(2027, 2, 2), "L", TestData.SpringTermId));
        await SeedAttendanceAsync(TestData.Alan, (new DateOnly(2026, 9, 11), "A", TestData.FallTermId));
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var all = await service.GetHistoryAsync(TestData.Ada, null, null, CancellationToken.None);

        Assert.Equal(
            [new DateOnly(2027, 2, 2), new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 10), new DateOnly(2026, 8, 20)],
            all.Select(r => r.AttendDate));
        Assert.Equal("Spring 2027", all[0].TermName);
        Assert.Equal("Late", all[0].AttendCodeDescription);
        Assert.Equal("Absent (unexcused)", all[1].AttendCodeDescription);
        Assert.True(all[1].IsAbsent);
        Assert.Null(all[3].TermName);

        var september = await service.GetHistoryAsync(TestData.Ada, new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), CancellationToken.None);
        Assert.Equal([new DateOnly(2026, 9, 11), new DateOnly(2026, 9, 10)], september.Select(r => r.AttendDate));

        var fromOnly = await service.GetHistoryAsync(TestData.Ada, new DateOnly(2026, 9, 11), null, CancellationToken.None);
        Assert.Equal(2, fromOnly.Count);

        var toOnly = await service.GetHistoryAsync(TestData.Ada, null, new DateOnly(2026, 9, 10), CancellationToken.None);
        Assert.Equal(2, toOnly.Count);
    }

    [Fact]
    public async Task GetAbsenteeismAsync_ThrowsNotFound_ForUnknownStudent()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<NotFoundException>(() => service.GetAbsenteeismAsync(999, null, CancellationToken.None));
    }

    [Fact]
    public async Task GetAbsenteeismAsync_ThrowsValidation_ForMalformedSchoolYear()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<AttendanceValidationException>(() =>
            service.GetAbsenteeismAsync(TestData.Ada, "2026-2028", CancellationToken.None));

        Assert.True(ex.Errors.ContainsKey("schoolYear"));
    }

    [Fact]
    public async Task GetAbsenteeismAsync_ZeroRecordedDays_GivesRateZeroAndNotChronic()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var status = await service.GetAbsenteeismAsync(TestData.Ada, "2026-2027", CancellationToken.None);

        Assert.Equal(0, status.TotalDaysRecorded);
        Assert.Equal(0, status.DaysAbsent);
        Assert.Equal(0m, status.AbsenceRatePercent);
        Assert.Equal(TestData.SchoolThreshold, status.AlertThreshold);
        Assert.False(status.IsChronicallyAbsent);
        Assert.False(status.HasUnresolvedChronicAbsenceAlert);
    }

    [Fact]
    public async Task GetAbsenteeismAsync_ExactlyAtThreshold_IsChronic()
    {
        await SeedAttendanceAsync(TestData.Ada,
            (new DateOnly(2026, 9, 1), "A", null),
            (new DateOnly(2026, 9, 2), "E", null),
            (new DateOnly(2026, 9, 3), "A", null),
            (new DateOnly(2026, 9, 4), "P", null),
            (new DateOnly(2026, 9, 5), "L", null));
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var status = await service.GetAbsenteeismAsync(TestData.Ada, "2026-2027", CancellationToken.None);

        Assert.Equal(5, status.TotalDaysRecorded);
        Assert.Equal(3, status.DaysAbsent);
        Assert.Equal(60.0m, status.AbsenceRatePercent);
        Assert.Equal(3, status.AlertThreshold);
        Assert.True(status.IsChronicallyAbsent);
    }

    [Fact]
    public async Task GetAbsenteeismAsync_JustBelowThreshold_IsNotChronic()
    {
        await SeedAttendanceAsync(TestData.Ada,
            (new DateOnly(2026, 9, 1), "A", null),
            (new DateOnly(2026, 9, 2), "A", null),
            (new DateOnly(2026, 9, 3), "P", null));
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var status = await service.GetAbsenteeismAsync(TestData.Ada, "2026-2027", CancellationToken.None);

        Assert.Equal(2, status.DaysAbsent);
        Assert.Equal(66.7m, status.AbsenceRatePercent);
        Assert.False(status.IsChronicallyAbsent);
    }

    [Fact]
    public async Task GetAbsenteeismAsync_CountsOnlyTheRequestedSchoolYear()
    {
        await SeedAttendanceAsync(TestData.Ada,
            (new DateOnly(2026, 3, 1), "A", null),
            (new DateOnly(2026, 3, 2), "A", null),
            (new DateOnly(2026, 8, 31), "A", null),
            (new DateOnly(2026, 9, 1), "A", null),
            (new DateOnly(2026, 9, 2), "P", null));
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var current = await service.GetAbsenteeismAsync(TestData.Ada, "2026-2027", CancellationToken.None);
        var previous = await service.GetAbsenteeismAsync(TestData.Ada, "2025-2026", CancellationToken.None);

        Assert.Equal((2, 1, false), (current.TotalDaysRecorded, current.DaysAbsent, current.IsChronicallyAbsent));
        Assert.Equal((3, 3, true), (previous.TotalDaysRecorded, previous.DaysAbsent, previous.IsChronicallyAbsent));
    }

    [Fact]
    public async Task GetAbsenteeismAsync_DefaultsToTheCurrentSchoolYear()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db); // clock fixed at 2026-11-03

        var status = await service.GetAbsenteeismAsync(TestData.Ada, null, CancellationToken.None);

        Assert.Equal("2026-2027", status.SchoolYear);
    }

    [Fact]
    public async Task GetAbsenteeismAsync_UsesConfiguredDefault_WhenSchoolHasNoThreshold()
    {
        await SeedAttendanceAsync(TestData.Linus,
            (new DateOnly(2026, 9, 1), "A", null),
            (new DateOnly(2026, 9, 2), "A", null));
        await using var db = _database.CreateContext();
        var service = CreateService(db, defaultThreshold: 2);

        var status = await service.GetAbsenteeismAsync(TestData.Linus, "2026-2027", CancellationToken.None);

        Assert.Equal(TestData.SchoolWithoutThresholdId, status.SchoolId);
        Assert.Equal(2, status.AlertThreshold);
        Assert.True(status.IsChronicallyAbsent);
    }

    [Fact]
    public async Task GetAbsenteeismAsync_ReportsUnresolvedAlertOnly()
    {
        await using (var setup = _database.CreateContext())
        {
            setup.StudentAlerts.AddRange(
                new StudentAlert { StudentId = TestData.Ada, SchoolId = TestData.SchoolId, AlertType = "CHRONIC_ABSENCE", SchoolYear = "2025-2026", AlertDate = TestData.Now.UtcDateTime, ResolvedDate = TestData.Now.UtcDateTime, ResolvedBy = "admin" },
                new StudentAlert { StudentId = TestData.Ada, SchoolId = TestData.SchoolId, AlertType = "CHRONIC_ABSENCE", SchoolYear = "2026-2027", AlertDate = TestData.Now.UtcDateTime });
            await setup.SaveChangesAsync();
        }

        await using var db = _database.CreateContext();
        var service = CreateService(db);

        Assert.True((await service.GetAbsenteeismAsync(TestData.Ada, "2026-2027", CancellationToken.None)).HasUnresolvedChronicAbsenceAlert);
        Assert.False((await service.GetAbsenteeismAsync(TestData.Ada, "2025-2026", CancellationToken.None)).HasUnresolvedChronicAbsenceAlert);
    }

    private static StudentAttendanceService CreateService(AttendanceDbContext db, int defaultThreshold = 10) =>
        new(
            db,
            Microsoft.Extensions.Options.Options.Create(new AbsenteeismOptions { DefaultAlertThreshold = defaultThreshold }),
            new FixedTimeProvider(TestData.Now));

    private async Task SeedAttendanceAsync(int studentId, params (DateOnly Date, string Code, int? TermId)[] rows)
    {
        await using var db = _database.CreateContext();
        var codes = await db.AttendanceCodes.ToDictionaryAsync(c => c.CodeValue);
        var schoolId = (await db.Students.SingleAsync(s => s.StudentId == studentId)).SchoolId;
        foreach (var (date, code, termId) in rows)
        {
            db.StudentAttendance.Add(new StudentAttendance
            {
                StudentId = studentId,
                SchoolId = schoolId,
                AttendDate = date,
                TermId = termId,
                AttendCode = code,
                IsAbsent = codes[code].IsAbsent,
                IsExcused = codes[code].IsExcused,
                MinutesLate = 0,
                CreatedDate = TestData.Now.UtcDateTime,
                CreatedBy = "seed",
            });
        }

        await db.SaveChangesAsync();
    }
}
