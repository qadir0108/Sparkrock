using System.ComponentModel.DataAnnotations;
using Attendance.Api.Services;

namespace Attendance.Api.Dtos;

/// <summary>Optional school-year selector for the absenteeism endpoint.</summary>
public sealed class AbsenteeismQuery : IValidatableObject
{
    /// <summary>School year label "YYYY-YYYY" (e.g. 2026-2027). Defaults to the current school year by the September rule.</summary>
    public string? SchoolYear { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SchoolYear is not null && !SchoolYearRule.TryParse(SchoolYear, out _))
        {
            yield return new ValidationResult(
                "'schoolYear' must be in the form YYYY-YYYY with consecutive years, e.g. 2026-2027.", [nameof(SchoolYear)]);
        }
    }
}
