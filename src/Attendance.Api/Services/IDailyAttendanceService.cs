using Attendance.Api.Dtos;

namespace Attendance.Api.Services;

public interface IDailyAttendanceService
{
    /// <summary>Saves one day of attendance for a batch of students atomically.</summary>
    /// <exception cref="NotFoundException">The school or one of the students does not exist.</exception>
    /// <exception cref="AttendanceValidationException">An attendance code is unknown or a student id is repeated.</exception>
    Task<SaveDailyAttendanceResponse> SaveAsync(SaveDailyAttendanceRequest request, CancellationToken cancellationToken);
}
