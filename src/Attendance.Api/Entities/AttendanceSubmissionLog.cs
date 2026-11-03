namespace Attendance.Api.Entities;

/// <summary>
/// Maps to the legacy <c>AttendanceSubmissionLog</c> table, which <c>sp_SaveDailyAttendance.sql:120-121</c>
/// writes to but <c>schema.sql</c> never creates. Created by <c>db/init/010_missing_tables.sql</c>.
/// </summary>
public sealed class AttendanceSubmissionLog
{
    public int LogId { get; set; }
    public int SchoolId { get; set; }
    public DateTime SubmittedDate { get; set; }
    public DateOnly AttendDate { get; set; }
    public int RecordCount { get; set; }
    public string? SubmittedBy { get; set; }
}
