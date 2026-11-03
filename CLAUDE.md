# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repository is

Two halves. `AttendanceSystem/` is a legacy VB6 daily-attendance module (one `.frm`, a `schema.sql`, two `sp_*.sql`). It is the **source of truth for the data model and is never modified**. Everything else (`src/`, `tests/`, `db/init/`, `Dockerfile`, `compose.yaml`) is the ASP.NET Core 8 Web API that replaces it. Git repository on `main` since 2026-11-03; do not commit or push unless asked.

Read `README.md` first (one page: architectural decisions and the legacy ambiguities and how each was resolved). `docs/legacy-review.md` is the full legacy analysis; `docs/api-review.md` is the ranked review; README section 3 tracks which findings are still open (H1 auth, M1 concurrency, M2 null entries, M3 absenteeism predicate, M4 no SQL Server tests, L1-L5).

## Commands

Host has .NET SDK 10 only (no 8 SDK), so projects target `net8.0` and build with the 10 SDK. Pass `--framework net8.0` to any `dotnet new`; do not add a `global.json`. Both projects set `TreatWarningsAsErrors`, so any warning fails the build.

```
dotnet build Attendance.sln
dotnet test Attendance.sln                                   # 74 tests, SQLite in-memory, no SQL Server needed
dotnet test tests/Attendance.Api.Tests --filter "FullyQualifiedName~DailyAttendanceServiceTests"
dotnet test tests/Attendance.Api.Tests --filter "Name=SaveAsync_RejectsUnknownAndInactiveCodes"
dotnet format Attendance.sln --verify-no-changes
```

Running the API locally needs `ConnectionStrings:Default`; startup throws without it. Set it with `dotnet user-secrets` (the csproj has a `UserSecretsId`) or `ConnectionStrings__Default`. Swagger is at `/swagger` in Development, health at `/health`.

Full stack in Docker (Docker Desktop must be running):

```
cp .env.example .env               # set MSSQL_SA_PASSWORD; add API_PORT=8088 if 8080 is taken (it is on this machine)
docker compose up -d --build       # sqlserver -> db-init (one-shot) -> api
docker compose logs api --tail 50
docker compose down                # add -v to drop the mssql-data volume and re-run db-init
docker build --target test .       # opt-in: run tests inside the image build
```

## Architecture

**Request flow.** Thin `[ApiController]` controllers in `src/Attendance.Api/Controllers` delegate to two scoped services: `IDailyAttendanceService` (POST `/api/attendance/daily`) and `IStudentAttendanceService` (GET `/api/students/{id}/attendance` and `/absenteeism`). DTOs in `Dtos/` implement `IValidatableObject`; services throw `AttendanceValidationException` (400) or `NotFoundException` (404), which `Infrastructure/GlobalExceptionHandler` maps to ProblemDetails. Anything else becomes a generic 500. Entities never leave the service layer.

**No stored-procedure calls and no EF migrations, deliberately.** The legacy `sp_SaveDailyAttendance` cannot be deployed (undefined `SchoolYear()` function, three tables missing from `schema.sql`, cursor variables never reset). `Services/DailyAttendanceService.cs` reimplements its rule in EF Core inside one transaction: upsert per `(StudentID, AttendDate)`, flags from `AttendanceCodes`, `TermID` from `SchoolTerms`, recount `IsAbsent` rows for the school year, upsert `StudentAttendanceSummary`, raise one unresolved `CHRONIC_ABSENCE` alert when the count reaches the school's threshold, log to `AttendanceSubmissionLog`. The history query reproduces result set 1 of `sp_GetStudentAttendance` in LINQ. `Program.cs` registers the DbContext without applying anything to the schema.

**Schema comes from scripts, so the EF model must match them by hand.** `db/init/init-db.sh` (compose `db-init` service, idempotent on database existence) runs legacy `schema.sql`, then `db/init/010_missing_tables.sql` (creates `Schools`, `SchoolTerms`, `AttendanceSubmissionLog` with columns inferred from procedure usage, plus a unique index on `StudentAttendance(StudentID, AttendDate)`), `020_reference_data.sql` (codes P/A/E/L, an assumption), the legacy `sp_*.sql` files (`sp_SaveDailyAttendance.sql` is allowed to fail), then `030_sample_data.sql`. `Data/AttendanceDbContext.cs` maps every entity to those column names and types explicitly (`DATE` to `DateOnly`, `DATETIME` via `HasColumnType("datetime")`, `VARCHAR` via `IsUnicode(false)`). A model change therefore means a matching change in `db/init/`, never in `AttendanceSystem/`, and nothing in `dotnet test` will catch drift because the tests build the schema from the model.

**Two rules live in one place each.** `Services/SchoolYearRule.cs` is the September school-year rule (`2026-09-01` and `2027-06-15` are both `"2026-2027"`); use it for every year label or range. Chronic absenteeism is a count, not a percentage: absent days in the school year `>= Schools.AbsenceAlertThreshold`, falling back to `Absenteeism:DefaultAlertThreshold` (10) only when the school row has no threshold. Excused absences count.

**Configuration** is the options pattern with `ValidateOnStart`: `Attendance:MaxBatchSize`, `Attendance:MaxFutureDays`, `Absenteeism:DefaultAlertThreshold`. Audit columns are written from `ICurrentUser` (the authenticated name, else `"api"`; there is no authentication yet) using UTC from `TimeProvider`.

**Tests** (`tests/Attendance.Api.Tests`) run on EF Core SQLite in-memory via `TestSupport/SqliteTestDatabase.cs` and `AttendanceApiFactory.cs` (`WebApplicationFactory<Program>`; `Program` is `public partial` for this). `FixedTimeProvider` and `StubCurrentUser` replace the real services. Because of SQLite, unique-index conflicts, deadlocks, collation and `sqlcmd` behaviour are only exercised by the Docker run.

## Agents

`.claude/agents/` defines four roles used throughout this project: `legacy` (read-only explorer of the VB6 code and SQL), `api-engineer` (builds and verifies the API and Docker stack), `api-reviewer` (read-only ranked review; hand its findings to `api-engineer` to fix), `azure-devops` (Azure Pipelines, Boards and az CLI automation; never edits app code or Docker files). Keep that split: neither reviewer nor legacy agent edits files.
