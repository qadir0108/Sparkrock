using Attendance.Api.Dtos;

namespace Attendance.Api.Services;

public interface IStudentAttendanceService
{
    /// <summary>Per-day history for a student, newest first, optionally bounded by an inclusive date range.</summary>
    /// <exception cref="NotFoundException">The student does not exist.</exception>
    /// <exception cref="AttendanceValidationException"><paramref name="from"/> is after <paramref name="to"/>.</exception>
    Task<IReadOnlyList<AttendanceHistoryRecordDto>> GetHistoryAsync(
        int studentId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken);

    /// <summary>Chronic-absence status for a student in a school year (default: the current one).</summary>
    /// <exception cref="NotFoundException">The student does not exist.</exception>
    /// <exception cref="AttendanceValidationException"><paramref name="schoolYear"/> is not "YYYY-YYYY".</exception>
    Task<StudentAbsenteeismResponse> GetAbsenteeismAsync(
        int studentId, string? schoolYear, CancellationToken cancellationToken);
}
