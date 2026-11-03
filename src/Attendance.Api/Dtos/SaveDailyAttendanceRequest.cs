using System.ComponentModel.DataAnnotations;
using Attendance.Api.Options;
using Microsoft.Extensions.Options;

namespace Attendance.Api.Dtos;

/// <summary>
/// One day of attendance for a set of students in one school: the legacy form's grid plus the
/// school and date it was captured for (frmDailyAttendance.frm cmdSave_Click).
/// </summary>
public sealed class SaveDailyAttendanceRequest : IValidatableObject
{
    /// <summary>Schools.SchoolID the batch belongs to.</summary>
    [Range(1, int.MaxValue)]
    public int SchoolId { get; init; }

    /// <summary>Attendance date (yyyy-MM-dd).</summary>
    [Required]
    public DateOnly? AttendDate { get; init; }

    /// <summary>One entry per student. Student ids must be unique within the batch.</summary>
    [Required]
    public List<DailyAttendanceEntryDto> Entries { get; init; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var options = (validationContext.GetService(typeof(IOptions<AttendanceOptions>)) as IOptions<AttendanceOptions>)?.Value
                      ?? new AttendanceOptions();
        var timeProvider = validationContext.GetService(typeof(TimeProvider)) as TimeProvider ?? TimeProvider.System;

        if (Entries is not { Count: > 0 })
        {
            yield return new ValidationResult("At least one attendance entry is required.", [nameof(Entries)]);
        }
        else
        {
            if (Entries.Count > options.MaxBatchSize)
            {
                yield return new ValidationResult(
                    $"A batch may contain at most {options.MaxBatchSize} entries.", [nameof(Entries)]);
            }

            var duplicates = Entries
                .Where(e => e is not null)
                .GroupBy(e => e.StudentId)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .Order()
                .ToArray();
            if (duplicates.Length > 0)
            {
                yield return new ValidationResult(
                    $"Duplicate student ids in batch: {string.Join(", ", duplicates)}.", [nameof(Entries)]);
            }
        }

        if (AttendDate is { } date)
        {
            var latestAllowed = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddDays(options.MaxFutureDays);
            if (date > latestAllowed)
            {
                yield return new ValidationResult(
                    $"AttendDate cannot be after {latestAllowed:yyyy-MM-dd}.", [nameof(AttendDate)]);
            }
        }
    }
}
