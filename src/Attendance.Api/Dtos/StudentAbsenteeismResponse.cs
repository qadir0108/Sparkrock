namespace Attendance.Api.Dtos;

/// <summary>
/// Chronic-absence status for one student and school year, using the legacy rule:
/// chronically absent when DaysAbsent (IsAbsent = 1 rows) >= the school's AbsenceAlertThreshold
/// (sp_GetStudentAttendance.sql:36-45, sp_SaveDailyAttendance.sql:97-115).
/// </summary>
public sealed record StudentAbsenteeismResponse(
    int StudentId,
    int SchoolId,
    string SchoolYear,
    int TotalDaysRecorded,
    int DaysAbsent,
    decimal AbsenceRatePercent,
    int AlertThreshold,
    bool IsChronicallyAbsent,
    bool HasUnresolvedChronicAbsenceAlert);
