namespace Attendance.Api.Dtos;

/// <summary>Outcome of a daily attendance save.</summary>
public sealed record SaveDailyAttendanceResponse(
    int SchoolId,
    DateOnly AttendDate,
    string SchoolYear,
    int? TermId,
    int RecordCount,
    int Inserted,
    int Updated,
    IReadOnlyList<SavedAttendanceEntryDto> Entries);

/// <summary>Per-student result of a daily attendance save.</summary>
public sealed record SavedAttendanceEntryDto(
    int AttendanceId,
    int StudentId,
    string AttendCode,
    bool IsAbsent,
    bool IsExcused,
    int MinutesLate,
    int TotalAbsencesThisSchoolYear,
    bool ChronicAbsenceAlertRaised);
