using Attendance.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Attendance.Api.Tests.TestSupport;

/// <summary>A private in-memory SQLite database created from the EF model; lives as long as the connection.</summary>
public sealed class SqliteTestDatabase : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public SqliteTestDatabase()
    {
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public DbContextOptions<AttendanceDbContext> Options =>
        new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options;

    public AttendanceDbContext CreateContext() => new(Options);

    public void Dispose() => _connection.Dispose();
}
