using System.ComponentModel.DataAnnotations;

namespace Attendance.Api.Options;

/// <summary>
/// Chronic-absence settings. Bound from the <c>Absenteeism</c> section. The rule itself comes from the
/// legacy procedures: a student is chronically absent when the school year's absence count reaches
/// <c>Schools.AbsenceAlertThreshold</c>, and <c>ISNULL(AbsenceAlertThreshold, 10)</c> supplies the default
/// (sp_SaveDailyAttendance.sql:98, sp_GetStudentAttendance.sql:40). <see cref="DefaultAlertThreshold"/>
/// replaces only that literal 10; a threshold stored on the school row always wins.
/// </summary>
public sealed class AbsenteeismOptions
{
    public const string SectionName = "Absenteeism";

    [Range(1, 1000)]
    public int DefaultAlertThreshold { get; init; } = 10;
}
