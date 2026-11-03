using Attendance.Api.Data;
using Attendance.Api.Infrastructure;
using Attendance.Api.Options;
using Attendance.Api.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException(
        "Connection string 'ConnectionStrings:Default' is not configured. " +
        "Set it with user-secrets or the ConnectionStrings__Default environment variable.");
}

builder.Services.AddOptions<AttendanceOptions>()
    .BindConfiguration(AttendanceOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<AbsenteeismOptions>()
    .BindConfiguration(AbsenteeismOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Schema is created by the legacy SQL scripts (compose db-init service), so no migrations are applied here.
builder.Services.AddDbContext<AttendanceDbContext>(options => options.UseSqlServer(connectionString));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();
builder.Services.AddScoped<IDailyAttendanceService, DailyAttendanceService>();
builder.Services.AddScoped<IStudentAttendanceService, StudentAttendanceService>();

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    var xmlDocs = Path.Combine(AppContext.BaseDirectory, "Attendance.Api.xml");
    if (File.Exists(xmlDocs))
    {
        options.IncludeXmlComments(xmlDocs, includeControllerXmlComments: true);
    }
});

builder.Services.AddHealthChecks()
    .AddSqlServer(connectionString, name: "sqlserver", timeout: TimeSpan.FromSeconds(5));

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapControllers();
app.MapHealthChecks("/health");

app.Run();

/// <summary>Exposed for WebApplicationFactory in the integration tests.</summary>
public partial class Program;
