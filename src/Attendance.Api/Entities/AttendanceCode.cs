namespace Attendance.Api.Entities;

/// <summary>Maps to the legacy <c>AttendanceCodes</c> table (schema.sql). Status values are data rows, not an enum.</summary>
public sealed class AttendanceCode
{
    public int CodeId { get; set; }
    public required string CodeValue { get; set; }
    public required string Description { get; set; }
    public bool IsAbsent { get; set; }
    public bool IsExcused { get; set; }
    public bool IsActive { get; set; } = true;
}
