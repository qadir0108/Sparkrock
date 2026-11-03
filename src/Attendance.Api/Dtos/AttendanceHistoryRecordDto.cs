namespace Attendance.Api.Dtos;

/// <summary>
/// One day of a student's history. Columns match result set 1 of sp_GetStudentAttendance.sql:17-35
/// (AttendDate, AttendCode, AttendCodeDescription, IsAbsent, IsExcused, MinutesLate, Notes, TermName).
/// </summary>
public sealed record AttendanceHistoryRecordDto(
    DateOnly AttendDate,
    string AttendCode,
    string AttendCodeDescription,
    bool IsAbsent,
    bool IsExcused,
    int? MinutesLate,
    string? Notes,
    string? TermName);
