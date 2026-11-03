# Sparkrock Attendance

ASP.NET Core 8 Web API replacing the legacy VB6 module in `AttendanceSystem/`. The legacy schema and procedures are the data-model source of truth and are never modified. The full legacy review (form anatomy, data flow, 26 rated risks, open questions) is in `docs/legacy-review.md`.

Endpoints: `POST /api/attendance/daily` (save a day's attendance for a set of students), `GET /api/students/{id}/attendance` (history), `GET /api/students/{id}/absenteeism` (chronic-absence status).

## 1. Key architectural decisions

| Decision | Why |
|---|---|
| Reimplement the save rule in EF Core inside one transaction instead of calling `sp_SaveDailyAttendance` (`src/Attendance.Api/Services/DailyAttendanceService.cs`) | The procedure cannot be deployed: it calls an undefined `SchoolYear()` function and three tables missing from `schema.sql`. It also never resets its cursor variables, so a re-save overwrites other students' rows, and it has no transaction. The code keeps the legacy sequence (upsert row, recount absences, upsert summary, raise `CHRONIC_ABSENCE` alert, log submission) and drops those bugs. |
| Reject unknown or inactive codes (400) | Legacy accepted any code silently, then hid those rows from history through an inner join on `AttendanceCodes`. Failing early keeps save and history consistent. |
| History is a LINQ query equivalent to result set 1 of `sp_GetStudentAttendance` (`StudentAttendanceService.cs`) | Avoids mapping two ADO-style result sets; absenteeism shares the row query but not the code join (see M3). |
| Chronic absenteeism = school-year absence count >= `Schools.AbsenceAlertThreshold`, falling back to 10 | This count-based rule is what both legacy procedures encode; it is not a percentage of days. Only the fallback literal is configurable (`Absenteeism:DefaultAlertThreshold`); a threshold on the school row always wins. |
| School year starts 1 September, labelled `YYYY-YYYY+1`, centralised in `SchoolYearRule` | Both procedures hardcode this. Matching it keeps labels compatible with any data the legacy code wrote. Applied to the row date, not the parameter, which fixes the legacy no-op filter. |
| Legacy `schema.sql` applied as-is plus an API-owned script for the missing tables, a unique index on `StudentAttendance(StudentID, AttendDate)` and seed data; no EF migrations | Keeps the legacy schema authoritative. Migrations would diverge from it. The unique index enforces what the legacy upsert assumed but never guaranteed. |
| Thin controllers, service layer, DTOs at the boundary, ProblemDetails via `IExceptionHandler`, options pattern, `/health`, Swagger in Development | Standard layering so business rules are testable without HTTP and entities never leave the API. |
| Tests run on EF Core SQLite in-memory; Docker Compose runs against real SQL Server 2022 | Fast tests with no database dependency; the SQL Server path is manual only (M4). |
| Request carries explicit `attendDate` and `schoolId`, capped by `Attendance:MaxFutureDays` and `MaxBatchSize` | The legacy form fixed the date to today with no back-dating. The API widens that deliberately. |

## 2. What the legacy code could not determine, and how the ambiguity was handled

| Unknown | Handling |
|---|---|
| DDL for `Schools`, `SchoolTerms`, `AttendanceSubmissionLog` (used by every procedure, defined nowhere) | Columns inferred from how the procedures read and write them. Created by the API-owned script and mapped as entities. Assumption; replace if the real DDL turns up. |
| The `SchoolYear()` SQL function | Never defined. Replaced by `SchoolYearRule` in code using the inline rule the procedures also contain. |
| `AttendanceCodes` seed values (the form only defaults to "P") | Seeded with P, A, E, L for the demo stack. "P" meaning Present is inference. |
| `sp_GetStudentsForAttendance`, called by the form but never defined | Out of scope. Nothing in the API depends on it. |
| How the connection string was meant to load (`config.ini` is passed as the server name) | Replaced by `ConnectionStrings:Default` from configuration or environment, with the SA password only in a git-ignored `.env`. |
| Audit identity (`SYSTEM_USER` is the shared SQL login) | `ICurrentUser` writes the authenticated user name, or "api" until authentication is added. |
| Whether the existing-row lookup should include `SchoolID` (legacy lookup ignores it, the recount filters by it, the summary key omits it) | Row identity is `(StudentID, AttendDate)` with `SchoolID` stored on the row; summary keyed on `(StudentID, SchoolYear)` to match the schema's `UQ_Summary`. |
| Whether excused absences count toward chronic status | Legacy counts every `IsAbsent = 1` row regardless of `IsExcused`. Kept as-is. |
| Missing `.vbp`, `Nz` helper, Crystal `.rpt`, target SQL Server version | Not needed by the API. The stack targets the SQL Server 2022 container image. |

## 3. Review findings (2026-11-03)

Verdict: ship after fixing the items below; no Critical defects. Full report with scenarios and fixes: [docs/api-review.md](docs/api-review.md).

| Severity | ID | Finding | Status |
|---|---|---|---|
| High | H1 | No authentication; endpoints write and enumerate student data | Open |
| Medium | M1 | Concurrent same-student saves collide on unique indexes, return 500 | Open |
| Medium | M2 | Null element in `entries` causes 500 instead of 400 | Open |
| Medium | M3 | Absenteeism and save path count absences with different predicates | Open |
| Medium | M4 | No test runs against real DDL or SQL Server | Open |
| Low | L1 | `attendDate` before 1900-09-01 returns 500 instead of 400 | Open |
| Low | L2 | SA password spliced unquoted into connection string | Open |
| Low | L3 | API connects as `sa` instead of least-privilege login | Open |
| Low | L4 | Interrupted db-init leaves half-built database never repaired | Open |
| Low | L5 | UTC audit timestamps and default school year undocumented | Open |
| Low | L6 | Host port 8080 hard-coded in compose | Resolved |
| Low | L7 | README omits that inactive attendance codes are rejected | Resolved |

Recommended first batch: H1 to M3 (authentication, conflict handling on concurrent saves, null-entry validation, one absence predicate for save and absenteeism).

## Run

```
cp .env.example .env            # set MSSQL_SA_PASSWORD; add API_PORT=8088 if 8080 is in use
docker compose up -d --build    # sqlserver -> db-init -> api on http://localhost:8080
dotnet test                     # no database needed
```
