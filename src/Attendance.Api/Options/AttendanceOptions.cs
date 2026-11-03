using System.ComponentModel.DataAnnotations;

namespace Attendance.Api.Options;

/// <summary>Limits applied to the daily attendance batch endpoint. Bound from the <c>Attendance</c> section.</summary>
public sealed class AttendanceOptions
{
    public const string SectionName = "Attendance";

    /// <summary>Maximum number of student entries accepted in one batch.</summary>
    [Range(1, 10_000)]
    public int MaxBatchSize { get; init; } = 1000;

    /// <summary>How many days past today (UTC) an AttendDate may be. 1 absorbs time-zone offsets east of UTC.</summary>
    [Range(0, 365)]
    public int MaxFutureDays { get; init; } = 1;
}
