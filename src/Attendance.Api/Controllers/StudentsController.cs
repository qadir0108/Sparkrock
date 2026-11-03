using Attendance.Api.Dtos;
using Attendance.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace Attendance.Api.Controllers;

[ApiController]
[Route("api/students/{studentId:int}")]
public sealed class StudentsController(IStudentAttendanceService studentAttendance) : ControllerBase
{
    /// <summary>Attendance history for one student, newest first, optionally bounded by from/to (inclusive).</summary>
    /// <response code="200">The records, or an empty list when the student has none.</response>
    /// <response code="400">from is after to, or a date is malformed.</response>
    /// <response code="404">The student does not exist.</response>
    [HttpGet("attendance")]
    [ProducesResponseType(typeof(IReadOnlyList<AttendanceHistoryRecordDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<AttendanceHistoryRecordDto>>> GetHistory(
        int studentId,
        [FromQuery] AttendanceHistoryQuery query,
        CancellationToken cancellationToken)
    {
        var records = await studentAttendance.GetHistoryAsync(studentId, query.From, query.To, cancellationToken);
        return Ok(records);
    }

    /// <summary>Chronic-absence status for one student in a school year (default: the current one).</summary>
    /// <response code="200">The status; zero recorded days gives rate 0 and not chronically absent.</response>
    /// <response code="400">schoolYear is not in the form YYYY-YYYY.</response>
    /// <response code="404">The student does not exist.</response>
    [HttpGet("absenteeism")]
    [ProducesResponseType(typeof(StudentAbsenteeismResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StudentAbsenteeismResponse>> GetAbsenteeism(
        int studentId,
        [FromQuery] AbsenteeismQuery query,
        CancellationToken cancellationToken)
    {
        var status = await studentAttendance.GetAbsenteeismAsync(studentId, query.SchoolYear, cancellationToken);
        return Ok(status);
    }
}
