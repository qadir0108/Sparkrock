namespace Attendance.Api.Entities;

/// <summary>
/// Maps to the legacy <c>Schools</c> table. The table is not declared in
/// <c>AttendanceSystem/Database/schema.sql</c>; its columns come from the references in
/// <c>sp_SaveDailyAttendance.sql</c>, <c>sp_GetStudentAttendance.sql</c> and <c>frmDailyAttendance.frm</c>.
/// </summary>
public sealed class School
{
    public int SchoolId { get; set; }
    public required string SchoolName { get; set; }
    public int? AbsenceAlertThreshold { get; set; }
    public bool Active { get; set; } = true;
}
