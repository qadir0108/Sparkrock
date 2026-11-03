namespace Attendance.Api.Entities;

/// <summary>Maps to the legacy <c>SchoolTerms</c> table (referenced by both stored procedures, absent from schema.sql).</summary>
public sealed class SchoolTerm
{
    public int TermId { get; set; }
    public int SchoolId { get; set; }
    public required string TermName { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
}
