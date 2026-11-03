using Attendance.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Attendance.Api.Tests.TestSupport;

/// <summary>
/// Boots the real Program with the SQL Server DbContext swapped for an in-memory SQLite database,
/// so the integration tests need no SQL Server. The clock is fixed at <see cref="TestData.Now"/>.
/// </summary>
public sealed class AttendanceApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", "Server=unused;Database=unused;User Id=unused;Password=unused");

        builder.ConfigureServices(services =>
        {
            // EF Core 8: AddDbContext captures the UseSqlServer action inside the DbContextOptions<T> factory;
            // dropping that registration and re-adding is enough to swap providers.
            services.RemoveAll<DbContextOptions<AttendanceDbContext>>();
            _connection.Open();
            services.AddDbContext<AttendanceDbContext>(options => options.UseSqlite(_connection));

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new FixedTimeProvider(TestData.Now));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();
        db.Database.EnsureCreated();
        TestData.SeedAsync(db).GetAwaiter().GetResult();

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}
