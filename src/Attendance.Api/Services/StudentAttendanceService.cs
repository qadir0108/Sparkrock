using Attendance.Api.Data;
using Attendance.Api.Dtos;
using Attendance.Api.Entities;
using Attendance.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Attendance.Api.Services;

/// <summary>
/// Read side for one student. The history query reproduces result set 1 of
/// <c>AttendanceSystem/Database/sp_GetStudentAttendance.sql</c> (lines 17-35) in LINQ instead of calling
/// the procedure: the procedure only accepts a single school year, is not composable for a date range,
/// and cannot run against the SQLite database used by the tests. Absenteeism is computed from the same
/// rows so the two endpoints cannot disagree.
/// </summary>
public sealed class StudentAttendanceService(
    AttendanceDbContext db,
    IOptions<AbsenteeismOptions> absenteeismOptions,
    TimeProvider timeProvider) : IStudentAttendanceService
{
    public async Task<IReadOnlyList<AttendanceHistoryRecordDto>> GetHistoryAsync(
        int studentId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        if (from is { } f && to is { } t && f > t)
        {
            throw new AttendanceValidationException("to", "'from' must be on or before 'to'.");
        }

        await EnsureStudentExistsAsync(studentId, cancellationToken);

        // Same joins and ordering as the legacy result set: INNER JOIN AttendanceCodes, LEFT JOIN SchoolTerms, date desc.
        var rows = AttendanceInRange(studentId, from, to);
        var query =
            from sa in rows
            join ac in db.AttendanceCodes.AsNoTracking() on sa.AttendCode equals ac.CodeValue
            join st in db.SchoolTerms.AsNoTracking() on sa.TermId equals st.TermId into terms
            from st in terms.DefaultIfEmpty()
            orderby sa.AttendDate descending
            select new AttendanceHistoryRecordDto(
                sa.AttendDate,
                sa.AttendCode,
                ac.Description,
                sa.IsAbsent,
                sa.IsExcused,
                sa.MinutesLate,
                sa.Notes,
                st != null ? st.TermName : null);

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<StudentAbsenteeismResponse> GetAbsenteeismAsync(
        int studentId, string? schoolYear, CancellationToken cancellationToken)
    {
        string year;
        if (schoolYear is null)
        {
            year = SchoolYearRule.For(DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime));
        }
        else if (!SchoolYearRule.TryParse(schoolYear, out var parsed))
        {
            throw new AttendanceValidationException("schoolYear",
                "'schoolYear' must be in the form YYYY-YYYY with consecutive years, e.g. 2026-2027.");
        }
        else
        {
            year = parsed;
        }

        var student = await db.Students.AsNoTracking()
            .FirstOrDefaultAsync(s => s.StudentId == studentId, cancellationToken)
            ?? throw new NotFoundException($"Student {studentId} was not found.");

        var (yearStart, yearEndExclusive) = SchoolYearRule.Range(year);
        var rows = AttendanceInRange(studentId, yearStart, yearEndExclusive.AddDays(-1));
        var totalDays = await rows.CountAsync(cancellationToken);
        var daysAbsent = await rows.CountAsync(a => a.IsAbsent, cancellationToken);

        // Threshold rule: Schools.AbsenceAlertThreshold, else the legacy literal 10 (configurable fallback).
        var schoolThreshold = await db.Schools.AsNoTracking()
            .Where(s => s.SchoolId == student.SchoolId)
            .Select(s => s.AbsenceAlertThreshold)
            .FirstOrDefaultAsync(cancellationToken);
        var threshold = schoolThreshold ?? absenteeismOptions.Value.DefaultAlertThreshold;

        var hasUnresolvedAlert = await db.StudentAlerts.AsNoTracking().AnyAsync(
            a => a.StudentId == studentId
                 && a.AlertType == DailyAttendanceService.ChronicAbsenceAlertType
                 && a.SchoolYear == year
                 && a.ResolvedDate == null,
            cancellationToken);

        var ratePercent = totalDays == 0 ? 0m : Math.Round(100m * daysAbsent / totalDays, 1);

        return new StudentAbsenteeismResponse(
            studentId,
            student.SchoolId,
            year,
            totalDays,
            daysAbsent,
            ratePercent,
            threshold,
            IsChronicallyAbsent: daysAbsent >= threshold,
            HasUnresolvedChronicAbsenceAlert: hasUnresolvedAlert);
    }

    private IQueryable<StudentAttendance> AttendanceInRange(int studentId, DateOnly? from, DateOnly? to)
    {
        var query = db.StudentAttendance.AsNoTracking().Where(a => a.StudentId == studentId);
        if (from is { } f)
        {
            query = query.Where(a => a.AttendDate >= f);
        }

        if (to is { } t)
        {
            query = query.Where(a => a.AttendDate <= t);
        }

        return query;
    }

    private async Task EnsureStudentExistsAsync(int studentId, CancellationToken cancellationToken)
    {
        if (!await db.Students.AsNoTracking().AnyAsync(s => s.StudentId == studentId, cancellationToken))
        {
            throw new NotFoundException($"Student {studentId} was not found.");
        }
    }
}
