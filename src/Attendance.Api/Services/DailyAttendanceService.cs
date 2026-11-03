using Attendance.Api.Data;
using Attendance.Api.Dtos;
using Attendance.Api.Entities;
using Attendance.Api.Infrastructure;
using Attendance.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Attendance.Api.Services;

/// <summary>
/// Implements the business rule of <c>AttendanceSystem/Database/sp_SaveDailyAttendance.sql</c> in EF Core.
/// The legacy procedure cannot be deployed (it calls an undefined <c>SchoolYear()</c> function and three
/// tables missing from schema.sql) and has no transaction, so the rule is reproduced here inside one
/// transaction. Line references below point at the legacy file. Bugs deliberately not carried over:
/// the never-reset <c>@ExistingID</c> that made later students overwrite an earlier student's row,
/// the never-reset <c>@IsAbsent/@IsExcused</c> that let unknown codes inherit flags, the misapplied
/// school-year filter in the recount, and silent acceptance of unknown codes (rejected with 400 here).
/// </summary>
public sealed class DailyAttendanceService(
    AttendanceDbContext db,
    IOptions<AbsenteeismOptions> absenteeismOptions,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<DailyAttendanceService> logger) : IDailyAttendanceService
{
    /// <summary>StudentAlerts.AlertType written by the legacy procedure (sp_SaveDailyAttendance.sql:104, 111).</summary>
    public const string ChronicAbsenceAlertType = "CHRONIC_ABSENCE";

    public async Task<SaveDailyAttendanceResponse> SaveAsync(SaveDailyAttendanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var attendDate = request.AttendDate ?? throw new AttendanceValidationException(nameof(request.AttendDate), "AttendDate is required.");
        var schoolId = request.SchoolId;
        var entries = request.Entries ?? [];
        if (entries.Count == 0)
        {
            throw new AttendanceValidationException(nameof(request.Entries), "At least one attendance entry is required.");
        }

        var studentIds = entries.Select(e => e.StudentId).Distinct().ToArray();
        if (studentIds.Length != entries.Count)
        {
            throw new AttendanceValidationException(nameof(request.Entries), "Student ids must be unique within a batch.");
        }

        // Referenced school and students must exist (the legacy procedure never checked; the form only listed real ones).
        var school = await db.Schools.AsNoTracking()
            .FirstOrDefaultAsync(s => s.SchoolId == schoolId, cancellationToken)
            ?? throw new NotFoundException($"School {schoolId} was not found.");

        var knownStudentIds = await db.Students.AsNoTracking()
            .Where(s => s.SchoolId == schoolId && studentIds.Contains(s.StudentId))
            .Select(s => s.StudentId)
            .ToListAsync(cancellationToken);
        var missingStudentIds = studentIds.Except(knownStudentIds).Order().ToArray();
        if (missingStudentIds.Length > 0)
        {
            throw new NotFoundException(
                $"Student(s) not found in school {schoolId}: {string.Join(", ", missingStudentIds)}.");
        }

        // Codes are data rows in AttendanceCodes (sp_SaveDailyAttendance.sql:46-48). Unknown codes are rejected, not defaulted.
        var activeCodes = await db.AttendanceCodes.AsNoTracking()
            .Where(c => c.IsActive)
            .ToListAsync(cancellationToken);
        var codesByValue = activeCodes.ToDictionary(c => c.CodeValue, StringComparer.OrdinalIgnoreCase);
        var unknownCodes = entries
            .Select(e => e.AttendCode.Trim())
            .Where(c => !codesByValue.ContainsKey(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unknownCodes.Length > 0)
        {
            throw new AttendanceValidationException(nameof(request.Entries),
                $"Unknown attendance code(s): {string.Join(", ", unknownCodes)}. " +
                $"Active codes: {string.Join(", ", activeCodes.Select(c => c.CodeValue).Order())}.");
        }

        // School year and term (sp_SaveDailyAttendance.sql:32-39).
        var schoolYear = SchoolYearRule.For(attendDate);
        var (yearStart, yearEnd) = SchoolYearRule.Range(schoolYear);
        var termId = await db.SchoolTerms.AsNoTracking()
            .Where(t => t.SchoolId == schoolId && attendDate >= t.StartDate && attendDate <= t.EndDate)
            .OrderBy(t => t.TermId)
            .Select(t => (int?)t.TermId)
            .FirstOrDefaultAsync(cancellationToken);

        var threshold = school.AbsenceAlertThreshold ?? absenteeismOptions.Value.DefaultAlertThreshold; // line 98: ISNULL(threshold, 10)
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var user = currentUser.Name;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // 1. Upsert one StudentAttendance row per (StudentID, AttendDate) (lines 54-77).
        var existingRows = await db.StudentAttendance
            .Where(a => a.AttendDate == attendDate && studentIds.Contains(a.StudentId))
            .ToDictionaryAsync(a => a.StudentId, cancellationToken);

        var inserted = 0;
        var updated = 0;
        var rowsByStudent = new Dictionary<int, StudentAttendance>(entries.Count);
        foreach (var entry in entries)
        {
            var code = codesByValue[entry.AttendCode.Trim()];
            var minutesLate = entry.MinutesLate ?? 0;
            var notes = NormalizeNotes(entry.Notes);

            if (existingRows.TryGetValue(entry.StudentId, out var row))
            {
                row.AttendCode = code.CodeValue;
                row.IsAbsent = code.IsAbsent;
                row.IsExcused = code.IsExcused;
                row.MinutesLate = minutesLate;
                row.Notes = notes;
                row.ModifiedDate = now;
                row.ModifiedBy = user;
                updated++;
            }
            else
            {
                row = new StudentAttendance
                {
                    StudentId = entry.StudentId,
                    SchoolId = schoolId,
                    AttendDate = attendDate,
                    TermId = termId,
                    AttendCode = code.CodeValue,
                    IsAbsent = code.IsAbsent,
                    IsExcused = code.IsExcused,
                    MinutesLate = minutesLate,
                    Notes = notes,
                    CreatedDate = now,
                    CreatedBy = user,
                };
                db.StudentAttendance.Add(row);
                inserted++;
            }

            rowsByStudent[entry.StudentId] = row;
        }

        await db.SaveChangesAsync(cancellationToken);

        // 2. Recount each student's absences for this school year at this school (lines 78-84, with the
        //    school-year filter applied to the row's AttendDate as intended).
        var absenceCounts = await db.StudentAttendance
            .Where(a => studentIds.Contains(a.StudentId)
                        && a.SchoolId == schoolId
                        && a.IsAbsent
                        && a.AttendDate >= yearStart
                        && a.AttendDate < yearEnd)
            .GroupBy(a => a.StudentId)
            .Select(g => new { StudentId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.StudentId, x => x.Count, cancellationToken);

        // 3. Upsert StudentAttendanceSummary(StudentID, SchoolYear) (lines 85-96).
        var summaries = await db.StudentAttendanceSummaries
            .Where(s => s.SchoolYear == schoolYear && studentIds.Contains(s.StudentId))
            .ToDictionaryAsync(s => s.StudentId, cancellationToken);

        // 4. Raise CHRONIC_ABSENCE once per student and year while unresolved (lines 97-115).
        var alreadyAlerted = (await db.StudentAlerts
                .Where(a => a.AlertType == ChronicAbsenceAlertType
                            && a.SchoolYear == schoolYear
                            && a.ResolvedDate == null
                            && studentIds.Contains(a.StudentId))
                .Select(a => a.StudentId)
                .ToListAsync(cancellationToken))
            .ToHashSet();
        var alertsRaised = new HashSet<int>();

        foreach (var studentId in studentIds)
        {
            var totalAbsences = absenceCounts.GetValueOrDefault(studentId);

            if (summaries.TryGetValue(studentId, out var summary))
            {
                summary.TotalAbsences = totalAbsences;
                summary.LastUpdated = now;
            }
            else
            {
                db.StudentAttendanceSummaries.Add(new StudentAttendanceSummary
                {
                    StudentId = studentId,
                    SchoolId = schoolId,
                    SchoolYear = schoolYear,
                    TotalAbsences = totalAbsences,
                    LastUpdated = now,
                });
            }

            if (totalAbsences >= threshold && !alreadyAlerted.Contains(studentId))
            {
                db.StudentAlerts.Add(new StudentAlert
                {
                    StudentId = studentId,
                    SchoolId = schoolId,
                    AlertType = ChronicAbsenceAlertType,
                    SchoolYear = schoolYear,
                    AlertDate = now,
                    AlertMessage = $"Student has reached {totalAbsences} absences this year",
                });
                alertsRaised.Add(studentId);
            }
        }

        // 5. Submission log (lines 120-121).
        db.AttendanceSubmissionLogs.Add(new AttendanceSubmissionLog
        {
            SchoolId = schoolId,
            SubmittedDate = now,
            AttendDate = attendDate,
            RecordCount = entries.Count,
            SubmittedBy = user,
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "Saved daily attendance for school {SchoolId} on {AttendDate}: {Inserted} inserted, {Updated} updated, {Alerts} chronic-absence alert(s) raised",
            schoolId, attendDate, inserted, updated, alertsRaised.Count);

        var savedEntries = entries
            .Select(e => rowsByStudent[e.StudentId])
            .Select(row => new SavedAttendanceEntryDto(
                row.AttendanceId,
                row.StudentId,
                row.AttendCode,
                row.IsAbsent,
                row.IsExcused,
                row.MinutesLate ?? 0,
                absenceCounts.GetValueOrDefault(row.StudentId),
                alertsRaised.Contains(row.StudentId)))
            .ToList();

        return new SaveDailyAttendanceResponse(
            schoolId, attendDate, schoolYear, termId, entries.Count, inserted, updated, savedEntries);
    }

    private static string? NormalizeNotes(string? notes)
    {
        var trimmed = notes?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
