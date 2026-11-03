namespace Attendance.Api.Entities;

/// <summary>Maps to the legacy <c>StudentAlerts</c> table (schema.sql).</summary>
public sealed class StudentAlert
{
    public int AlertId { get; set; }
    public int StudentId { get; set; }
    public int SchoolId { get; set; }
    public required string AlertType { get; set; }
    public required string SchoolYear { get; set; }
    public DateTime AlertDate { get; set; }
    public string? AlertMessage { get; set; }
    public DateTime? ResolvedDate { get; set; }
    public string? ResolvedBy { get; set; }
}
