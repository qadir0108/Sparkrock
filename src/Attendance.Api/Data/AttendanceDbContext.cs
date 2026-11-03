using Attendance.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Attendance.Api.Data;

/// <summary>
/// EF Core model over the legacy schema. Table and column names are the legacy ones from
/// <c>AttendanceSystem/Database/schema.sql</c> (plus the three tables that file omits but the
/// procedures use). The schema is created by the legacy scripts via <c>db-init</c>, not by migrations.
/// </summary>
public sealed class AttendanceDbContext(DbContextOptions<AttendanceDbContext> options) : DbContext(options)
{
    public DbSet<School> Schools => Set<School>();
    public DbSet<SchoolTerm> SchoolTerms => Set<SchoolTerm>();
    public DbSet<Student> Students => Set<Student>();
    public DbSet<AttendanceCode> AttendanceCodes => Set<AttendanceCode>();
    public DbSet<StudentAttendance> StudentAttendance => Set<StudentAttendance>();
    public DbSet<StudentAttendanceSummary> StudentAttendanceSummaries => Set<StudentAttendanceSummary>();
    public DbSet<StudentAlert> StudentAlerts => Set<StudentAlert>();
    public DbSet<AttendanceSubmissionLog> AttendanceSubmissionLogs => Set<AttendanceSubmissionLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<School>(e =>
        {
            e.ToTable("Schools");
            e.HasKey(x => x.SchoolId);
            e.Property(x => x.SchoolId).HasColumnName("SchoolID");
            e.Property(x => x.SchoolName).HasMaxLength(200).IsUnicode(false);
        });

        modelBuilder.Entity<SchoolTerm>(e =>
        {
            e.ToTable("SchoolTerms");
            e.HasKey(x => x.TermId);
            e.Property(x => x.TermId).HasColumnName("TermID");
            e.Property(x => x.SchoolId).HasColumnName("SchoolID");
            e.Property(x => x.TermName).HasMaxLength(100).IsUnicode(false);
        });

        modelBuilder.Entity<Student>(e =>
        {
            e.ToTable("Students");
            e.HasKey(x => x.StudentId);
            e.Property(x => x.StudentId).HasColumnName("StudentID");
            e.Property(x => x.SchoolId).HasColumnName("SchoolID");
            e.Property(x => x.FirstName).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.LastName).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.Grade).HasMaxLength(10).IsUnicode(false);
            e.Property(x => x.CreatedDate).HasColumnType("datetime");
        });

        modelBuilder.Entity<AttendanceCode>(e =>
        {
            e.ToTable("AttendanceCodes");
            e.HasKey(x => x.CodeId);
            e.Property(x => x.CodeId).HasColumnName("CodeID");
            e.Property(x => x.CodeValue).HasMaxLength(5).IsUnicode(false);
            e.HasIndex(x => x.CodeValue).IsUnique();
            e.Property(x => x.Description).HasMaxLength(100).IsUnicode(false);
        });

        modelBuilder.Entity<StudentAttendance>(e =>
        {
            e.ToTable("StudentAttendance");
            e.HasKey(x => x.AttendanceId);
            e.Property(x => x.AttendanceId).HasColumnName("AttendanceID");
            e.Property(x => x.StudentId).HasColumnName("StudentID");
            e.Property(x => x.SchoolId).HasColumnName("SchoolID");
            e.Property(x => x.TermId).HasColumnName("TermID");
            e.Property(x => x.AttendCode).HasMaxLength(5).IsUnicode(false);
            e.Property(x => x.Notes).HasMaxLength(500).IsUnicode(false);
            e.Property(x => x.CreatedDate).HasColumnType("datetime");
            e.Property(x => x.CreatedBy).HasMaxLength(100).IsUnicode(false);
            e.Property(x => x.ModifiedDate).HasColumnType("datetime");
            e.Property(x => x.ModifiedBy).HasMaxLength(100).IsUnicode(false);
            // Added by db/init/010_missing_tables.sql: the legacy upsert assumes one row per student per day.
            e.HasIndex(x => new { x.StudentId, x.AttendDate })
                .IsUnique()
                .HasDatabaseName("UX_StudentAttendance_StudentID_AttendDate");
        });

        modelBuilder.Entity<StudentAttendanceSummary>(e =>
        {
            e.ToTable("StudentAttendanceSummary");
            e.HasKey(x => x.SummaryId);
            e.Property(x => x.SummaryId).HasColumnName("SummaryID");
            e.Property(x => x.StudentId).HasColumnName("StudentID");
            e.Property(x => x.SchoolId).HasColumnName("SchoolID");
            e.Property(x => x.SchoolYear).HasMaxLength(9).IsUnicode(false);
            e.Property(x => x.LastUpdated).HasColumnType("datetime");
            e.HasIndex(x => new { x.StudentId, x.SchoolYear }).IsUnique().HasDatabaseName("UQ_Summary");
        });

        modelBuilder.Entity<StudentAlert>(e =>
        {
            e.ToTable("StudentAlerts");
            e.HasKey(x => x.AlertId);
            e.Property(x => x.AlertId).HasColumnName("AlertID");
            e.Property(x => x.StudentId).HasColumnName("StudentID");
            e.Property(x => x.SchoolId).HasColumnName("SchoolID");
            e.Property(x => x.AlertType).HasMaxLength(50).IsUnicode(false);
            e.Property(x => x.SchoolYear).HasMaxLength(9).IsUnicode(false);
            e.Property(x => x.AlertDate).HasColumnType("datetime");
            e.Property(x => x.AlertMessage).HasMaxLength(500).IsUnicode(false);
            e.Property(x => x.ResolvedDate).HasColumnType("datetime");
            e.Property(x => x.ResolvedBy).HasMaxLength(100).IsUnicode(false);
        });

        modelBuilder.Entity<AttendanceSubmissionLog>(e =>
        {
            e.ToTable("AttendanceSubmissionLog");
            e.HasKey(x => x.LogId);
            e.Property(x => x.LogId).HasColumnName("LogID");
            e.Property(x => x.SchoolId).HasColumnName("SchoolID");
            e.Property(x => x.SubmittedDate).HasColumnType("datetime");
            e.Property(x => x.SubmittedBy).HasMaxLength(100).IsUnicode(false);
        });
    }
}
