using Attendance.Api.Data;
using Attendance.Api.Entities;

namespace Attendance.Api.Tests.TestSupport;

/// <summary>Deterministic seed shared by the service and integration tests.</summary>
public static class TestData
{
    public const int SchoolId = 1;
    public const int SchoolWithoutThresholdId = 2;
    public const int SchoolThreshold = 3;

    public const int Ada = 101;
    public const int Alan = 102;
    public const int Grace = 103;
    public const int Linus = 201; // belongs to school 2

    public const int FallTermId = 1;
    public const int SpringTermId = 2;

    public static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public static async Task SeedAsync(AttendanceDbContext db)
    {
        db.Schools.AddRange(
            new School { SchoolId = SchoolId, SchoolName = "Test School", AbsenceAlertThreshold = SchoolThreshold },
            new School { SchoolId = SchoolWithoutThresholdId, SchoolName = "Other School", AbsenceAlertThreshold = null });

        db.SchoolTerms.AddRange(
            new SchoolTerm { TermId = FallTermId, SchoolId = SchoolId, TermName = "Fall 2026", StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2027, 1, 31) },
            new SchoolTerm { TermId = SpringTermId, SchoolId = SchoolId, TermName = "Spring 2027", StartDate = new DateOnly(2027, 2, 1), EndDate = new DateOnly(2027, 6, 30) });

        db.Students.AddRange(
            new Student { StudentId = Ada, SchoolId = SchoolId, FirstName = "Ada", LastName = "Lovelace", Grade = "3" },
            new Student { StudentId = Alan, SchoolId = SchoolId, FirstName = "Alan", LastName = "Turing", Grade = "3" },
            new Student { StudentId = Grace, SchoolId = SchoolId, FirstName = "Grace", LastName = "Hopper", Grade = "4" },
            new Student { StudentId = Linus, SchoolId = SchoolWithoutThresholdId, FirstName = "Linus", LastName = "Torvalds", Grade = "5" });

        db.AttendanceCodes.AddRange(
            new AttendanceCode { CodeValue = "P", Description = "Present" },
            new AttendanceCode { CodeValue = "A", Description = "Absent (unexcused)", IsAbsent = true },
            new AttendanceCode { CodeValue = "E", Description = "Absent (excused)", IsAbsent = true, IsExcused = true },
            new AttendanceCode { CodeValue = "L", Description = "Late" },
            new AttendanceCode { CodeValue = "X", Description = "Retired code", IsActive = false });

        await db.SaveChangesAsync();
    }
}
