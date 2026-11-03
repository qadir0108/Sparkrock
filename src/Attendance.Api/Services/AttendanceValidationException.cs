namespace Attendance.Api.Services;

/// <summary>
/// Input is well-formed but semantically invalid (for example an unknown attendance code).
/// Mapped to a 400 <c>ValidationProblemDetails</c> by the global exception handler.
/// </summary>
public sealed class AttendanceValidationException : Exception
{
    public AttendanceValidationException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }

    public AttendanceValidationException(IDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public IDictionary<string, string[]> Errors { get; }
}
