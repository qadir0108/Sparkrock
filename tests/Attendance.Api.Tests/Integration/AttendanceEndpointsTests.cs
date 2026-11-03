using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Attendance.Api.Dtos;
using Attendance.Api.Tests.TestSupport;

namespace Attendance.Api.Tests.Integration;

/// <summary>End-to-end through the real pipeline (routing, model validation, exception handler) on SQLite.</summary>
public sealed class AttendanceEndpointsTests(AttendanceApiFactory factory) : IClassFixture<AttendanceApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task PostDaily_EmptyEntries_Returns400ValidationProblem()
    {
        var response = await _client.PostAsJsonAsync("/api/attendance/daily", new
        {
            schoolId = TestData.SchoolId,
            attendDate = "2026-10-01",
            entries = Array.Empty<object>(),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(problem.GetProperty("errors").TryGetProperty("Entries", out _));
    }

    [Fact]
    public async Task PostDaily_DuplicateStudentIds_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/attendance/daily", new
        {
            schoolId = TestData.SchoolId,
            attendDate = "2026-10-01",
            entries = new[]
            {
                new { studentId = TestData.Ada, attendCode = "P" },
                new { studentId = TestData.Ada, attendCode = "A" },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        var messages = problem.GetProperty("errors").GetProperty("Entries").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(messages, m => m!.Contains("Duplicate student ids"));
    }

    [Fact]
    public async Task PostDaily_MalformedDate_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/attendance/daily", new
        {
            schoolId = TestData.SchoolId,
            attendDate = "not-a-date",
            entries = new[] { new { studentId = TestData.Ada, attendCode = "P" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Contains(problem.GetProperty("errors").EnumerateObject(), p => p.Name.Contains("attendDate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PostDaily_UnknownCode_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/attendance/daily", new
        {
            schoolId = TestData.SchoolId,
            attendDate = "2026-10-01",
            entries = new[] { new { studentId = TestData.Ada, attendCode = "ZZ" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.True(problem.GetProperty("errors").TryGetProperty("Entries", out _));
    }

    [Fact]
    public async Task PostDaily_UnknownSchool_Returns404ProblemDetails()
    {
        var response = await _client.PostAsJsonAsync("/api/attendance/daily", new
        {
            schoolId = 999,
            attendDate = "2026-10-01",
            entries = new[] { new { studentId = TestData.Ada, attendCode = "P" } },
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Equal(404, problem.GetProperty("status").GetInt32());
        Assert.Contains("School 999", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task PostDaily_ThenHistoryAndAbsenteeism_ReflectTheSavedBatch()
    {
        var post = await _client.PostAsJsonAsync("/api/attendance/daily", new
        {
            schoolId = TestData.SchoolId,
            attendDate = "2026-10-05",
            entries = new[] { new { studentId = TestData.Grace, attendCode = "a", minutesLate = 0, notes = "dentist" } },
        });

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var saved = await post.Content.ReadFromJsonAsync<SaveDailyAttendanceResponse>();
        Assert.NotNull(saved);
        Assert.Equal(1, saved.Inserted);
        Assert.Equal("2026-2027", saved.SchoolYear);
        Assert.Equal("A", Assert.Single(saved.Entries).AttendCode);

        var history = await _client.GetFromJsonAsync<List<AttendanceHistoryRecordDto>>($"/api/students/{TestData.Grace}/attendance?from=2026-10-01&to=2026-10-31");
        Assert.NotNull(history);
        var record = Assert.Single(history);
        Assert.Equal(new DateOnly(2026, 10, 5), record.AttendDate);
        Assert.Equal("Absent (unexcused)", record.AttendCodeDescription);
        Assert.Equal("Fall 2026", record.TermName);
        Assert.Equal("dentist", record.Notes);

        var status = await _client.GetFromJsonAsync<StudentAbsenteeismResponse>($"/api/students/{TestData.Grace}/absenteeism");
        Assert.NotNull(status);
        Assert.Equal("2026-2027", status.SchoolYear);
        Assert.Equal(1, status.TotalDaysRecorded);
        Assert.Equal(1, status.DaysAbsent);
        Assert.Equal(100m, status.AbsenceRatePercent);
        Assert.Equal(TestData.SchoolThreshold, status.AlertThreshold);
        Assert.False(status.IsChronicallyAbsent);
    }

    [Fact]
    public async Task GetHistory_FromAfterTo_Returns400ValidationProblem()
    {
        var response = await _client.GetAsync($"/api/students/{TestData.Ada}/attendance?from=2026-10-02&to=2026-10-01");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.True(problem.GetProperty("errors").TryGetProperty("From", out _));
    }

    [Fact]
    public async Task GetHistory_MalformedDate_Returns400()
    {
        var response = await _client.GetAsync($"/api/students/{TestData.Ada}/attendance?from=yesterday");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetHistory_UnknownStudent_Returns404()
    {
        var response = await _client.GetAsync("/api/students/999/attendance");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.Contains("Student 999", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task GetHistory_StudentWithoutRecords_Returns200EmptyList()
    {
        var history = await _client.GetFromJsonAsync<List<AttendanceHistoryRecordDto>>($"/api/students/{TestData.Alan}/attendance");

        Assert.NotNull(history);
        Assert.Empty(history);
    }

    [Fact]
    public async Task GetAbsenteeism_MalformedSchoolYear_Returns400ValidationProblem()
    {
        var response = await _client.GetAsync($"/api/students/{TestData.Ada}/absenteeism?schoolYear=2026-2028");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await ReadProblemAsync(response);
        Assert.True(problem.GetProperty("errors").TryGetProperty("SchoolYear", out _));
    }

    [Fact]
    public async Task GetAbsenteeism_UnknownStudent_Returns404()
    {
        var response = await _client.GetAsync("/api/students/999/absenteeism");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<JsonElement> ReadProblemAsync(HttpResponseMessage response)
    {
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.Clone();
    }
}
