using System.ComponentModel.DataAnnotations;

namespace Attendance.Api.Dtos;

/// <summary>One grid row of the legacy form: student, code, minutes late, notes.</summary>
public sealed class DailyAttendanceEntryDto
{
    /// <summary>Students.StudentID.</summary>
    [Range(1, int.MaxValue)]
    public int StudentId { get; init; }

    /// <summary>AttendanceCodes.CodeValue, e.g. "P". Matched case-insensitively against active codes.</summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(5, MinimumLength = 1)]
    public string AttendCode { get; init; } = string.Empty;

    /// <summary>Minutes late; defaults to 0 like the legacy form.</summary>
    [Range(0, 1440)]
    public int? MinutesLate { get; init; }

    /// <summary>Free-text remarks (StudentAttendance.Notes, 500 characters).</summary>
    [StringLength(500)]
    public string? Notes { get; init; }
}
