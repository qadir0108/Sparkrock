namespace Attendance.Api.Entities;

/// <summary>Maps to the legacy <c>StudentAttendance</c> table (schema.sql): one row per student per day.</summary>
public sealed class StudentAttendance
{
    public int AttendanceId { get; set; }
    public int StudentId { get; set; }
    public int SchoolId { get; set; }
    public DateOnly AttendDate { get; set; }
    public int? TermId { get; set; }
    public required string AttendCode { get; set; }
    public bool IsAbsent { get; set; }
    public bool IsExcused { get; set; }
    public int? MinutesLate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedDate { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedBy { get; set; }
}
