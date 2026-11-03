using System.ComponentModel.DataAnnotations;

namespace Attendance.Api.Dtos;

/// <summary>Optional inclusive date bounds for a student's attendance history.</summary>
public sealed class AttendanceHistoryQuery : IValidatableObject
{
    /// <summary>Earliest AttendDate to include (inclusive, yyyy-MM-dd).</summary>
    public DateOnly? From { get; init; }

    /// <summary>Latest AttendDate to include (inclusive, yyyy-MM-dd).</summary>
    public DateOnly? To { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (From is { } from && To is { } to && from > to)
        {
            yield return new ValidationResult("'from' must be on or before 'to'.", [nameof(From), nameof(To)]);
        }
    }
}
