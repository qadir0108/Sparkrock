using Attendance.Api.Data;
using Attendance.Api.Dtos;
using Attendance.Api.Options;
using Attendance.Api.Services;
using Attendance.Api.Tests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Attendance.Api.Tests.Services;

public sealed class DailyAttendanceServiceTests : IDisposable
{
    private static readonly DateOnly Day1 = new(2026, 9, 14);
    private static readonly DateOnly Day2 = new(2026, 9, 15);
    private static readonly DateOnly Day3 = new(2026, 9, 16);
    private static readonly DateOnly Day4 = new(2026, 9, 17);

    private readonly SqliteTestDatabase _database = new();

    public DailyAttendanceServiceTests()
    {
        using var db = _database.CreateContext();
        TestData.SeedAsync(db).GetAwaiter().GetResult();
    }

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task SaveAsync_InsertsOneRowPerStudent_WithFlagsTermAuditSummaryAndLog()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);
        var request = new SaveDailyAttendanceRequest
        {
            SchoolId = TestData.SchoolId,
            AttendDate = Day1,
            Entries =
            [
                new DailyAttendanceEntryDto { StudentId = TestData.Ada, AttendCode = "P" },
                new DailyAttendanceEntryDto { StudentId = TestData.Alan, AttendCode = "A", Notes = "  sick  " },
                new DailyAttendanceEntryDto { StudentId = TestData.Grace, AttendCode = "E", MinutesLate = 5 },
            ],
        };

        var result = await service.SaveAsync(request, CancellationToken.None);

        Assert.Equal(3, result.RecordCount);
        Assert.Equal(3, result.Inserted);
        Assert.Equal(0, result.Updated);
        Assert.Equal("2026-2027", result.SchoolYear);
        Assert.Equal(TestData.FallTermId, result.TermId);
        Assert.Equal([TestData.Ada, TestData.Alan, TestData.Grace], result.Entries.Select(e => e.StudentId));

        await using var verify = _database.CreateContext();
        var rows = await verify.StudentAttendance.OrderBy(a => a.StudentId).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal(TestData.SchoolId, r.SchoolId);
            Assert.Equal(Day1, r.AttendDate);
            Assert.Equal(TestData.FallTermId, r.TermId);
            Assert.Equal("test-user", r.CreatedBy);
            Assert.Equal(TestData.Now.UtcDateTime, r.CreatedDate);
            Assert.Null(r.ModifiedDate);
            Assert.Null(r.ModifiedBy);
        });
        Assert.Equal(("P", false, false, 0, null), Flags(rows[0]));
        Assert.Equal(("A", true, false, 0, "sick"), Flags(rows[1]));
        Assert.Equal(("E", true, true, 5, null), Flags(rows[2]));

        var summaries = await verify.StudentAttendanceSummaries.OrderBy(s => s.StudentId).ToListAsync();
        Assert.Equal([0, 1, 1], summaries.Select(s => s.TotalAbsences));
        Assert.All(summaries, s => Assert.Equal("2026-2027", s.SchoolYear));

        var log = Assert.Single(await verify.AttendanceSubmissionLogs.ToListAsync());
        Assert.Equal(3, log.RecordCount);
        Assert.Equal(Day1, log.AttendDate);
        Assert.Equal("test-user", log.SubmittedBy);
    }

    [Fact]
    public async Task SaveAsync_ResavingSameDay_UpdatesInPlace_AndNeverTouchesOtherStudentsRows()
    {
        // Legacy sp_SaveDailyAttendance never reset @ExistingID, so a student without a row that followed a
        // student with one would overwrite the first student's row. This test pins the corrected behaviour.
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var first = await service.SaveAsync(Request(Day1, (TestData.Ada, "A")), CancellationToken.None);
        var adaRowId = Assert.Single(first.Entries).AttendanceId;

        var second = await service.SaveAsync(
            Request(Day1, (TestData.Ada, "P"), (TestData.Alan, "A"), (TestData.Grace, "L")), CancellationToken.None);

        Assert.Equal(2, second.Inserted);
        Assert.Equal(1, second.Updated);

        await using var verify = _database.CreateContext();
        var rows = await verify.StudentAttendance.OrderBy(a => a.StudentId).ToListAsync();
        Assert.Equal(3, rows.Count);

        var ada = rows.Single(r => r.StudentId == TestData.Ada);
        Assert.Equal(adaRowId, ada.AttendanceId);
        Assert.Equal("P", ada.AttendCode);
        Assert.False(ada.IsAbsent);
        Assert.Equal("test-user", ada.ModifiedBy);
        Assert.Equal(TestData.Now.UtcDateTime, ada.ModifiedDate);

        var alan = rows.Single(r => r.StudentId == TestData.Alan);
        Assert.Equal("A", alan.AttendCode);
        Assert.True(alan.IsAbsent);
        Assert.Null(alan.ModifiedDate);

        var summaries = await verify.StudentAttendanceSummaries.ToDictionaryAsync(s => s.StudentId, s => s.TotalAbsences);
        Assert.Equal(0, summaries[TestData.Ada]);
        Assert.Equal(1, summaries[TestData.Alan]);
        Assert.Equal(0, summaries[TestData.Grace]);
        Assert.Equal(2, await verify.AttendanceSubmissionLogs.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_RaisesChronicAbsenceAlertOnce_WhenSchoolThresholdIsReached()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var afterTwo = await service.SaveAsync(Request(Day2, (TestData.Ada, "A")), CancellationToken.None);
        await service.SaveAsync(Request(Day1, (TestData.Ada, "A")), CancellationToken.None);
        Assert.False(afterTwo.Entries[0].ChronicAbsenceAlertRaised);
        Assert.Equal(0, await db.StudentAlerts.CountAsync());

        var atThreshold = await service.SaveAsync(Request(Day3, (TestData.Ada, "E")), CancellationToken.None);

        Assert.Equal(TestData.SchoolThreshold, atThreshold.Entries[0].TotalAbsencesThisSchoolYear);
        Assert.True(atThreshold.Entries[0].ChronicAbsenceAlertRaised);
        var alert = Assert.Single(await db.StudentAlerts.ToListAsync());
        Assert.Equal(DailyAttendanceService.ChronicAbsenceAlertType, alert.AlertType);
        Assert.Equal("2026-2027", alert.SchoolYear);
        Assert.Equal(TestData.Ada, alert.StudentId);
        Assert.Equal("Student has reached 3 absences this year", alert.AlertMessage);
        Assert.Null(alert.ResolvedDate);

        var aboveThreshold = await service.SaveAsync(Request(Day4, (TestData.Ada, "A")), CancellationToken.None);

        Assert.False(aboveThreshold.Entries[0].ChronicAbsenceAlertRaised);
        Assert.Equal(4, aboveThreshold.Entries[0].TotalAbsencesThisSchoolYear);
        Assert.Equal(1, await db.StudentAlerts.CountAsync());
        Assert.Equal(4, (await db.StudentAttendanceSummaries.SingleAsync(s => s.StudentId == TestData.Ada)).TotalAbsences);
    }

    [Fact]
    public async Task SaveAsync_UsesConfiguredDefaultThreshold_WhenSchoolHasNone()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db, defaultThreshold: 1);
        var request = new SaveDailyAttendanceRequest
        {
            SchoolId = TestData.SchoolWithoutThresholdId,
            AttendDate = Day1,
            Entries = [new DailyAttendanceEntryDto { StudentId = TestData.Linus, AttendCode = "A" }],
        };

        var result = await service.SaveAsync(request, CancellationToken.None);

        Assert.True(result.Entries[0].ChronicAbsenceAlertRaised);
        Assert.Null(result.TermId); // school 2 has no terms
        Assert.Equal(1, await db.StudentAlerts.CountAsync(a => a.StudentId == TestData.Linus));
    }

    [Fact]
    public async Task SaveAsync_RecountsOnlyTheSchoolYearOfTheDate()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        await service.SaveAsync(Request(new DateOnly(2026, 5, 10), (TestData.Ada, "A")), CancellationToken.None);
        await service.SaveAsync(Request(new DateOnly(2026, 6, 10), (TestData.Ada, "A")), CancellationToken.None);
        var result = await service.SaveAsync(Request(new DateOnly(2026, 9, 10), (TestData.Ada, "A")), CancellationToken.None);

        Assert.Equal(1, result.Entries[0].TotalAbsencesThisSchoolYear);
        var summaries = await db.StudentAttendanceSummaries.ToDictionaryAsync(s => s.SchoolYear, s => s.TotalAbsences);
        Assert.Equal(2, summaries["2025-2026"]);
        Assert.Equal(1, summaries["2026-2027"]);
    }

    [Fact]
    public async Task SaveAsync_RollsBackTheWholeBatch_WhenALaterStepFails()
    {
        await using (var setup = _database.CreateContext())
        {
            // Make the final step (submission log insert) fail, after attendance rows and summaries were written.
            await setup.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER fail_submission_log BEFORE INSERT ON AttendanceSubmissionLog BEGIN SELECT RAISE(ABORT, 'simulated failure'); END;");
        }

        await using var db = _database.CreateContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            service.SaveAsync(Request(Day1, (TestData.Ada, "A"), (TestData.Alan, "P")), CancellationToken.None));

        await using var verify = _database.CreateContext();
        Assert.Equal(0, await verify.StudentAttendance.CountAsync());
        Assert.Equal(0, await verify.StudentAttendanceSummaries.CountAsync());
        Assert.Equal(0, await verify.StudentAlerts.CountAsync());
        Assert.Equal(0, await verify.AttendanceSubmissionLogs.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_ThrowsNotFound_ForUnknownSchool()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);
        var request = new SaveDailyAttendanceRequest
        {
            SchoolId = 999,
            AttendDate = Day1,
            Entries = [new DailyAttendanceEntryDto { StudentId = TestData.Ada, AttendCode = "P" }],
        };

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => service.SaveAsync(request, CancellationToken.None));

        Assert.Contains("School 999", ex.Message);
        Assert.Equal(0, await db.StudentAttendance.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_ThrowsNotFound_ListingStudentsMissingFromTheSchool()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);
        // 999 does not exist; 201 exists but belongs to another school.
        var request = Request(Day1, (TestData.Ada, "P"), (999, "P"), (TestData.Linus, "P"));

        var ex = await Assert.ThrowsAsync<NotFoundException>(() => service.SaveAsync(request, CancellationToken.None));

        Assert.Contains("201, 999", ex.Message);
        Assert.Equal(0, await db.StudentAttendance.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_RejectsUnknownAndInactiveCodes()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);
        var request = Request(Day1, (TestData.Ada, "Z"), (TestData.Alan, "X"), (TestData.Grace, "P"));

        var ex = await Assert.ThrowsAsync<AttendanceValidationException>(() => service.SaveAsync(request, CancellationToken.None));

        var message = Assert.Single(ex.Errors[nameof(SaveDailyAttendanceRequest.Entries)]);
        Assert.Contains("Z", message);
        Assert.Contains("X", message);
        Assert.Contains("Active codes: A, E, L, P", message);
        Assert.Equal(0, await db.StudentAttendance.CountAsync());
    }

    [Fact]
    public async Task SaveAsync_MatchesCodesCaseInsensitively_AndStoresTheCanonicalValue()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var result = await service.SaveAsync(Request(Day1, (TestData.Ada, " a ")), CancellationToken.None);

        Assert.Equal("A", result.Entries[0].AttendCode);
        Assert.True(result.Entries[0].IsAbsent);
        Assert.Equal("A", (await db.StudentAttendance.SingleAsync()).AttendCode);
    }

    [Fact]
    public async Task SaveAsync_RejectsDuplicateStudentIds_EvenWithoutModelValidation()
    {
        await using var db = _database.CreateContext();
        var service = CreateService(db);

        var ex = await Assert.ThrowsAsync<AttendanceValidationException>(() =>
            service.SaveAsync(Request(Day1, (TestData.Ada, "P"), (TestData.Ada, "A")), CancellationToken.None));

        Assert.True(ex.Errors.ContainsKey(nameof(SaveDailyAttendanceRequest.Entries)));
    }

    private static DailyAttendanceService CreateService(AttendanceDbContext db, int defaultThreshold = 10) =>
        new(
            db,
            Microsoft.Extensions.Options.Options.Create(new AbsenteeismOptions { DefaultAlertThreshold = defaultThreshold }),
            new StubCurrentUser(),
            new FixedTimeProvider(TestData.Now),
            NullLogger<DailyAttendanceService>.Instance);

    private static SaveDailyAttendanceRequest Request(DateOnly date, params (int StudentId, string Code)[] entries) => new()
    {
        SchoolId = TestData.SchoolId,
        AttendDate = date,
        Entries = entries.Select(e => new DailyAttendanceEntryDto { StudentId = e.StudentId, AttendCode = e.Code }).ToList(),
    };

    private static (string Code, bool IsAbsent, bool IsExcused, int? MinutesLate, string? Notes) Flags(Api.Entities.StudentAttendance row) =>
        (row.AttendCode, row.IsAbsent, row.IsExcused, row.MinutesLate, row.Notes);
}
