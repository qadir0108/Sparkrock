namespace Attendance.Api.Entities;

/// <summary>Maps to the legacy <c>Students</c> table (schema.sql).</summary>
public sealed class Student
{
    public int StudentId { get; set; }
    public int SchoolId { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string? Grade { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public bool Active { get; set; } = true;
    public DateTime CreatedDate { get; set; }
}
