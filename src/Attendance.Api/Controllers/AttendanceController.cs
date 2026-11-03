using Attendance.Api.Dtos;
using Attendance.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Api.Controllers;

[ApiController]
[Route("api/attendance")]
public sealed class AttendanceController(IDailyAttendanceService dailyAttendance) : ControllerBase
{
    /// <summary>Saves one day of attendance for a set of students in a school (upsert per student and day).</summary>
    /// <response code="200">The batch was saved; one result entry per student.</response>
    /// <response code="400">Validation failed (empty batch, duplicate student, unknown code, bad date).</response>
    /// <response code="404">The school or one of the students does not exist.</response>
    [HttpPost("daily")]
    [ProducesResponseType(typeof(SaveDailyAttendanceResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SaveDailyAttendanceResponse>> SaveDaily(
        [FromBody] SaveDailyAttendanceRequest request,
        CancellationToken cancellationToken)
    {
        var result = await dailyAttendance.SaveAsync(request, cancellationToken);
        return Ok(result);
    }
}
