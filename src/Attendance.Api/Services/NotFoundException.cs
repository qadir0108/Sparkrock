namespace Attendance.Api.Services;

/// <summary>A referenced school or student does not exist. Mapped to 404 by the global exception handler.</summary>
public sealed class NotFoundException(string message) : Exception(message);
