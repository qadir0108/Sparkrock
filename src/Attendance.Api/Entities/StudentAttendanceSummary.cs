namespace Attendance.Api.Entities;

/// <summary>Maps to the legacy <c>StudentAttendanceSummary</c> table (schema.sql), unique per (StudentID, SchoolYear).</summary>
public sealed class StudentAttendanceSummary
{
    public int SummaryId { get; set; }
    public int StudentId { get; set; }
    public int SchoolId { get; set; }
    public required string SchoolYear { get; set; }
    public int TotalAbsences { get; set; }
    public DateTime LastUpdated { get; set; }
}
