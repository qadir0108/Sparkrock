# API Review

Review date: 2026-11-03. Scope: `src/Attendance.Api`, `tests/Attendance.Api.Tests`, `Dockerfile`, `.dockerignore`, `compose.yaml`, `.env.example`, `.gitignore`, `.gitattributes`, `db/init/*`, `README.md`, checked against `AttendanceSystem/Database/schema.sql` and the two `sp_*.sql` files (`AttendanceSystem/Forms` out of scope). Reviewer: the read-only api-reviewer agent; findings are reported, not fixed.

**Verdict: ship after fixing the listed items.** No Critical defects; one High (no authentication on endpoints that write and enumerate student data, acknowledged in the README as pending but not declared intentional), four Medium items with contained fixes, and seven Low items. Build, tests, format and the Docker image build all pass. Every entity matches the legacy DDL plus `db/init/010_missing_tables.sql` column for column (names, types, lengths, nullability, keys, the `UX_StudentAttendance_StudentID_AttendDate` unique index and `UQ_Summary`); the EF Core save path reproduces the legacy rule and the four legacy bugs it claims to fix are fixed and pinned by tests.

> **Status note (added when this review was saved):** L6 was already resolved by the api-engineer while the review was in progress. `compose.yaml:43` now publishes `${API_PORT:-8080}:8080`, `.env.example` documents `API_PORT`, and the README Run block mentions it. L7 was resolved when these findings were added to the README: section 1 row 2 now reads "Reject unknown or inactive codes (400)". All other findings are open and summarised in README section 3.

## Findings

### Critical

None.

### High

**H1. No authentication or authorization on any endpoint** — `src/Attendance.Api/Program.cs:35-63`, `compose.yaml:41-44`
- Defect: nothing registers authentication or authorization; `POST /api/attendance/daily` writes attendance for any `schoolId`, and `GET /api/students/{id}/attendance` and `/absenteeism` accept sequential integer ids, so every student's record can be enumerated. Compose publishes the API on the host with `ASPNETCORE_ENVIRONMENT=Development` (Swagger UI exposed).
- Scenario: any client that can reach port 8080 posts `{"schoolId":1,"attendDate":"2026-10-05","entries":[{"studentId":1,"attendCode":"A"}]}` and the row is written with `CreatedBy = "api"`; `GET /api/students/1/attendance` through `/api/students/N/attendance` dumps the whole school.
- Status: confirmed. `README.md:30` says the audit name is `"api"` "until authentication is added"; that acknowledges the gap but does not state the API is intentionally open.
- Fix: add bearer-token (or at minimum API-key) authentication with a fallback `RequireAuthenticatedUser` policy and `[AllowAnonymous]` only on `/health`; until that lands, add an explicit README statement that the API must run behind an authenticating gateway and must not be exposed directly.

### Medium

**M1. Concurrent saves for the same student collide on the unique indexes and surface as a generic 500; the recount can deadlock** — `src/Attendance.Api/Services/DailyAttendanceService.cs:93-98,136,147-155,184,219`
- Defect: the existing-row lookup (line 96) and the summary lookup (line 158) run under the default READ COMMITTED isolation with no lock, then insert. The second writer violates `UX_StudentAttendance_StudentID_AttendDate` or `UQ_Summary`, EF throws `DbUpdateException`, `GlobalExceptionHandler` maps it to `500 "An unexpected error occurred."`, and the whole batch (every other student in it) is rolled back with no hint to retry. Separately, two transactions that each insert an `IsAbsent` row for the same student on different days and then run the recount (line 147) each block on the other's uncommitted row; SQL Server picks a deadlock victim (error 1205), again a 500.
- Scenario: two teachers submit the same class for 2026-10-05 within the same second, neither row exists yet. Both reads return empty, both insert; the second `SaveChangesAsync` at line 143 fails with SqlException 2627 → 500 for a request whose payload was valid.
- Status: confirmed by reading for the duplicate-key path (deterministic given the unique index; the SQLite suite cannot exercise it); plausible for the deadlock.
- Fix: inside the transaction take an application lock scoped to the school and date (`EXEC sp_getapplock @Resource = 'attendance:{schoolId}:{date}', @LockMode = 'Exclusive', @LockOwner = 'Transaction'` via `ExecuteSqlInterpolatedAsync`) so same-day batches serialize, map `SqlException` 2601/2627 to `409 Conflict` with a "retry" detail, and retry once on 1205. If `EnableRetryOnFailure` is adopted, wrap the whole method in `db.Database.CreateExecutionStrategy().ExecuteAsync` because the explicit transaction otherwise disables it.

**M2. A `null` element inside `entries` is accepted by validation and crashes the service with a 500** — `src/Attendance.Api/Services/DailyAttendanceService.cs:41`, `src/Attendance.Api/Dtos/SaveDailyAttendanceRequest.cs:43-49`
- Defect: MVC does not validate null collection elements, the DTO validator deliberately skips them (`Where(e => e is not null)`, line 44) instead of rejecting them, and the service dereferences `e.StudentId` on line 41 → `NullReferenceException`.
- Scenario (executed through the real pipeline on SQLite): `{"schoolId":1,"attendDate":"2026-10-01","entries":[{"studentId":101,"attendCode":"P"},null]}` → `500 {"title":"An unexpected error occurred."}`. Expected 400.
- Status: confirmed.
- Fix: in `SaveDailyAttendanceRequest.Validate`, yield `"Entries must not contain null elements."` for `Entries` when `Entries.Any(e => e is null)`, and have the service throw `AttendanceValidationException` on the same condition as a defence.

**M3. Absenteeism endpoint and save path count absences with different predicates, so they can disagree** — `src/Attendance.Api/Services/DailyAttendanceService.cs:147-152` vs `src/Attendance.Api/Services/StudentAttendanceService.cs:76-85,108-122`; `README.md:13`
- Defect: the save recount filters `a.SchoolId == schoolId` (legacy line 82) and writes that number to `StudentAttendanceSummary` and the alert; the absenteeism endpoint counts every row for the student in the year with no `SchoolId` filter, does not read the persisted summary at all, and takes the threshold from the student's current `Students.SchoolId` rather than the school the summary belongs to (legacy joined `Schools` on `sas.SchoolID`). Absenteeism also skips the `INNER JOIN AttendanceCodes` that history applies, so rows with a code absent from `AttendanceCodes` count toward `TotalDaysRecorded`/`DaysAbsent` but are invisible in `/attendance`, contradicting README row 3 ("one data path so they cannot disagree").
- Scenario: student 101 has 6 `A` rows at school 1 in 2026-2027, is transferred (`Students.SchoolID = 2`), then 5 `A` rows are saved at school 2. Save path: `TotalAbsencesThisSchoolYear = 5`, summary 5, no alert (threshold 10). `GET /api/students/101/absenteeism` → `DaysAbsent = 11`, `IsChronicallyAbsent = true`, `HasUnresolvedChronicAbsenceAlert = false`.
- Status: confirmed by reading (requires a mid-year school change, which the README section 2 lists as an open ambiguity for the save path only).
- Fix: use one predicate in both places — simplest is `AttendanceInRange(...).Where(a => a.SchoolId == student.SchoolId)` in `GetAbsenteeismAsync` and the same `AttendanceCodes` join as history (or document that absenteeism counts raw rows), and read the threshold from that same school; alternatively have absenteeism return `StudentAttendanceSummary.TotalAbsences` as the legacy procedure did.

**M4. No test runs against the real DDL or SQL Server, so model/DDL drift and SQL Server-only behaviour are invisible to `dotnet test`** — `tests/Attendance.Api.Tests/TestSupport/SqliteTestDatabase.cs:16`, `tests/Attendance.Api.Tests/TestSupport/AttendanceApiFactory.cs:44`, `README.md:18`
- Defect: both fixtures create the schema from the EF model with `EnsureCreated()` on SQLite, so the suite validates the model against itself; a wrong column name, a `datetime2` vs `datetime` mismatch, or a table missing from `010_missing_tables.sql` would pass every test and fail only at runtime in compose. SQL Server-specific behaviour (M1's unique-index conflicts and deadlocks, case-insensitive collation on `AttendCode`, `datetime` precision, `sqlcmd` script errors) is also unexercised. README row "Docker Compose verifies against real SQL Server 2022 … one real end-to-end path" describes a manual step; nothing automated performs it.
- Scenario: rename `e.Property(x => x.TermId).HasColumnName("TermID")` to `"Term_ID"` in `AttendanceDbContext.cs:70`; `dotnet test` stays green (74/74), the container returns 500 on every save.
- Status: confirmed (every entity was compared to the DDL by hand during this review and no drift was found; the gap is that nothing guards it).
- Fix: add an opt-in integration test class gated on an `ATTENDANCE_TEST_SQLSERVER` connection string (or Testcontainers.MsSql) that applies `schema.sql` + `db/init/*.sql` and runs save → history → absenteeism, skipped with a clear message when unset; or add a `verify` compose service that curls the three endpoints after `db-init`.

### Low

**L1. `AttendDate` before 1900-09-01 produces a 500** — `src/Attendance.Api/Services/SchoolYearRule.cs:21-30,49`, `src/Attendance.Api/Dtos/SaveDailyAttendanceRequest.cs:57-65`
- Defect: `SchoolYearRule.For` happily yields `"0-1"` or `"1899-1900"`, `Range` then calls `TryParse`, which rejects anything outside `^\d{4}-\d{4}$` with `first >= 1900`, and throws `ArgumentException`. Validation only caps the future side.
- Scenario (executed): `"attendDate":"0001-01-01"` → 500. Expected 400.
- Status: confirmed.
- Fix: reject `AttendDate < new DateOnly(1900, 9, 1)` (or a configurable `Attendance:MinAttendDate`) in `Validate`, and make `For`/`TryParse` agree on the supported range.

**L2. SA password spliced unquoted into the connection string** — `compose.yaml:45`, `.env.example:1-3`
- Defect: `Password=${MSSQL_SA_PASSWORD}` breaks for any value containing `;`, `'` or `"` (symbols SQL Server complexity rules encourage), and a `$` in `.env` must be written `$$`; `.env.example` does not say so.
- Scenario: `MSSQL_SA_PASSWORD=Ab1!x;yz9` → SQL Server starts, `db-init` succeeds (sqlcmd takes the value as an argument), the API fails at startup with a connection-string parse error.
- Status: plausible.
- Fix: write `Password='${MSSQL_SA_PASSWORD}'` in compose and document in `.env.example` that `'`, `;` and `$` are not supported.

**L3. API and db-init both run as `sa`** — `compose.yaml:45`, `src/Attendance.Api/appsettings.Development.json:3`
- Defect: the API connection uses the sysadmin login; a bug or injection anywhere in the API has server-level rights.
- Status: confirmed.
- Fix: in `init-db.sh` create a dedicated login/user with `db_datareader`/`db_datawriter` on `Attendance` and point `ConnectionStrings__Default` at it; keep `sa` only for `db-init`.

**L4. db-init idempotency is keyed on database existence, so an interrupted first run leaves a half-built database that is never repaired** — `db/init/init-db.sh:42-46,48-52`
- Defect: the `ERR` trap drops the database on script failure, but a `SIGTERM` (`docker compose down` or Ctrl-C during the first `up`) is not an error; the partially created `Attendance` database persists in `mssql-data`, and every later run prints "already exists; nothing to do".
- Scenario: Ctrl-C while `schema.sql` is running → next `docker compose up` → `db-init` exits 0 → API returns 500 on every request (`Invalid object name 'Schools'`).
- Status: plausible.
- Fix: decide completion on a marker written as the last step (e.g. an extended property or a `__DbInit` row) rather than `sys.databases`, and `trap ... EXIT` dropping the database when the marker is absent.

**L5. UTC is used for audit timestamps and for the "current school year" default; neither is documented** — `src/Attendance.Api/Services/DailyAttendanceService.cs:90`, `src/Attendance.Api/Services/StudentAttendanceService.cs:59`
- Defect: `CreatedDate`, `ModifiedDate`, `LastUpdated`, `AlertDate`, `SubmittedDate` are written as UTC into `datetime` columns the legacy filled with `GETDATE()` (server-local), and `GetAbsenteeismAsync` picks the default school year from the UTC date.
- Scenario: a school in UTC-5 calls `/absenteeism` without `schoolYear` at 20:00 local on 2026-08-31; UTC is already 2026-09-01, so the API reports `2026-2027` with zero days while the school is still in `2025-2026`. The `MaxFutureDays = 1` window on save (`SaveDailyAttendanceRequest.cs:59`) correctly absorbs offsets east of UTC.
- Status: confirmed by reading; one-evening-per-year window.
- Fix: state in the README that audit columns are UTC; for the default year, either document the UTC convention or accept an optional `asOf` date / configured school time zone.

**L6. Host port 8080 is hard-coded and collides on this host** — `compose.yaml:42`, `README.md:39` — **RESOLVED, see status note above**
- Defect: `"8080:8080"` cannot be overridden; on this machine `127.0.0.1:8080` is already served by a RavenDB instance, so `curl http://localhost:8080/health` returns RavenDB's `RouteNotFoundException` JSON (HTTP 400) even while `sparkrock-api-1` is up, and README's "api on http://localhost:8080" is wrong here.
- Status: confirmed on this host at review time; environment-specific. Fixed concurrently by the api-engineer.
- Fix (applied): `"${API_PORT:-8080}:8080"` in compose and an `API_PORT` line in `.env.example`.

**L7. README says unknown codes are rejected; inactive codes are rejected too** — `src/Attendance.Api/Services/DailyAttendanceService.cs:64-66`, `README.md:12`
- Defect: `Where(c => c.IsActive)` means a known but retired `AttendanceCodes` row is refused with 400, which the legacy procedure (and the README) did not do. Reasonable behaviour, but undocumented.
- Status: confirmed (pinned by `DailyAttendanceServiceTests.SaveAsync_RejectsUnknownAndInactiveCodes`).
- Fix: one clause in README row 2 ("unknown or inactive").

## Verification

- `dotnet build Attendance.sln` — pass: `Build succeeded. 0 Warning(s) 0 Error(s)` (`TreatWarningsAsErrors` on in both projects; SDK 10 building net8.0, as expected).
- `dotnet test Attendance.sln --no-build` — pass: `Passed: 74, Failed: 0, Skipped: 0` (1 s).
- `dotnet format Attendance.sln --verify-no-changes` — pass, exit 0, no drift.
- `docker compose config -q` — pass, exit 0.
- `docker info` — daemon up; `docker build --target final .` — pass, exit 0 (layers cached from the engineer's build).
- Line endings — `db/init/init-db.sh`, all `db/init/*.sql`, `AttendanceSystem/Database/*.sql`, `Dockerfile`, `compose.yaml`, `.env.example` are LF (CR grep negative; `file` reports plain ASCII).
- Anti-pattern grep over `src/` and `tests/` — no `FromSqlRaw`/`ExecuteSqlRaw` in `src/` (one `ExecuteSqlRawAsync` with a constant string in a test), no `.Result`/`.Wait()`/`async void`/`Task.Run`, no CORS, no `catch` blocks; `GetAwaiter().GetResult()` only in test constructors; `TrustServerCertificate=True` only in `appsettings.Development.json` and `compose.yaml`; no password in any `appsettings*.json`; `.env` present, listed in `.gitignore:2` and `.dockerignore:7` (contents not printed).
- Schema alignment — every entity in `src/Attendance.Api/Entities` and its mapping in `src/Attendance.Api/Data/AttendanceDbContext.cs` compared by hand to `schema.sql` and `db/init/010_missing_tables.sql`: no mismatches in column names, types (`DATE`→`DateOnly`, `DATETIME`→`HasColumnType("datetime")`, `VARCHAR(n)`→`HasMaxLength(n).IsUnicode(false)`), nullability, identity keys, `UQ_Summary`, or the `(StudentID, AttendDate)` unique index. Legacy rule reproduction checked step by step against `sp_SaveDailyAttendance.sql:32-121`: upsert per `(StudentID, AttendDate)`, flags from `AttendanceCodes`, `TermID` from `SchoolTerms` (ordered by `TermID` for determinism), September label, per-school-year recount of `IsAbsent = 1`, summary upsert on `(StudentID, SchoolYear)`, `>=` threshold with `ISNULL(…, 10)` fallback, no-duplicate unresolved alert, legacy alert message text, one `AttendanceSubmissionLog` row with `RecordCount = entries.Count`; all inside one `BeginTransactionAsync`. The stale-`@ExistingID`, stale-flag, lifetime-count and no-transaction bugs are fixed and covered by `DailyAttendanceServiceTests` lines 83, 172 and 188.
- HTTP edge-case probes — eight requests through `WebApplicationFactory<Program>` on SQLite from a throwaway probe project outside the repository (no repo files touched): batch over `Attendance:MaxBatchSize` → 400 with the configured limit (options reach `IValidatableObject` through the pipeline); `attendDate` 5 days ahead → 400; `attendDate` with a time component → 400; over-posted `isAbsent`/`createdBy`/`attendanceId` on an entry → ignored, 200; whitespace-only `attendCode` → 400; `null` entry element → **500** (M2); `attendDate` `0001-01-01` → **500** (L1); `/health` with an unreachable SQL Server → 503.
- Running stack — observed the engineer's stack transiently (`sparkrock-sqlserver-1` healthy, `sparkrock-db-init-1` Exited (0), `sparkrock-api-1` Up, volume `sparkrock_mssql-data`), but it was torn down before `docker compose logs` or a catalogue query could run; `GET http://localhost:8080/health` from the host reached RavenDB, not the API (L6). No `docker compose up`/`down` was run by the reviewer.

## Not reviewed

- `init-db.sh` and the legacy scripts executed against real SQL Server (sqlcmd flags, the non-fatal `sp_SaveDailyAttendance` path, seed data) — verified by reading only; the live stack disappeared before its logs could be read. The api-engineer's own run reported these steps succeeding, with `sp_SaveDailyAttendance` failing non-fatally on error 195 as expected.
- EF model versus the SQL Server catalogue at runtime, and M1's concurrency behaviour (unique-index conflicts, deadlocks) on SQL Server — reasoned from the DDL and isolation defaults, not executed.
- `AttendanceSystem/Forms/` (out of scope) and the contents of `.env` (deliberately not printed).
