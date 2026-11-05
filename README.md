# Sparkrock Attendance

ASP.NET Core 8 Web API replacing the legacy VB6 module in `AttendanceSystem/`. The legacy schema and procedures are the data-model source of truth and are never modified. Full legacy analysis: [docs/legacy-review.md](docs/legacy-review.md).

**Endpoints**

| Method | Path | Description |
|---|---|---|
| `POST` | `/api/attendance/daily` | Save a day's attendance for a set of students |
| `GET` | `/api/students/{id}/attendance` | Attendance history |
| `GET` | `/api/students/{id}/absenteeism` | Chronic-absence status |

## Quick start

```
cp .env.example .env            # set MSSQL_SA_PASSWORD; add API_PORT=8088 if 8080 is in use
docker compose up -d --build    # sqlserver -> db-init -> api on http://localhost:${API_PORT:-8080}
dotnet test                     # 74 tests; no database needed
```

Swagger is at `/swagger` (Development only). Health check at `/health`.

---

## 1. Architectural decisions (ADRs)

### 1a. Data model and schema

| Decision | Why |
|---|---|
| Apply `schema.sql` as-is; `db/init/010_missing_tables.sql` creates the three tables the procedures reference but never define (`Schools`, `SchoolTerms`, `AttendanceSubmissionLog`) | Keeps the legacy schema authoritative. EF migrations would diverge from it; there is no safe baseline to migrate from. |
| Unique index on `StudentAttendance(StudentID, AttendDate)` | The legacy upsert assumed uniqueness but never enforced it. Added in `010_missing_tables.sql`. |
| Row identity is `(StudentID, AttendDate)` with `SchoolID` stored on the row; summary keyed `(StudentID, SchoolYear)` to match `UQ_Summary` | The legacy lookup ignores `SchoolID` on the row read, but the recount and the schema key differ. Matching `UQ_Summary` exactly avoids a silent constraint violation on re-save. |
| School year starts 1 September, labelled `YYYY-YYYY+1`, centralised in `SchoolYearRule` | Both procedures hardcode this boundary. Consistent labels preserve compatibility with any rows the legacy code already wrote. |
| Chronic absenteeism = school-year absence count `>=` `Schools.AbsenceAlertThreshold` (fallback: `Absenteeism:DefaultAlertThreshold = 10`) | This count-based rule is what both legacy procedures encode — it is not a percentage of days attended. The threshold on the school row always wins; the config fallback applies only when the row has none. |

### 1b. Service layer

| Decision | Why |
|---|---|
| Reimplement the save rule in EF Core inside one transaction; do not call `sp_SaveDailyAttendance` (`Services/DailyAttendanceService.cs`) | The procedure cannot be deployed: it calls an undefined `SchoolYear()` function, references three tables missing from `schema.sql`, never resets its cursor variable (a re-save corrupts other students' rows in the same batch), and has no transaction. The service keeps the same sequence (upsert row → recount absences → upsert summary → raise alert → log submission) and drops these bugs. |
| Reject unknown **or inactive** codes with 400 | Legacy accepted any code silently, then hid those rows via an inner join on `AttendanceCodes`. Failing early keeps save and history consistent; inactive codes are covered by `DailyAttendanceServiceTests.SaveAsync_RejectsUnknownAndInactiveCodes`. |
| History is a LINQ query equivalent to result set 1 of `sp_GetStudentAttendance` (`StudentAttendanceService.cs`) | Avoids mapping two ADO-style result sets; absenteeism shares the row query but not the code join (see M3). |
| Thin controllers → service layer → DTOs at the boundary → `ProblemDetails` via `IExceptionHandler` | Business rules are testable without HTTP; entities never leave the API; all exception-to-status mapping is in one place. |
| Request carries explicit `attendDate` and `schoolId`, capped by `MaxFutureDays` and `MaxBatchSize` | The legacy form fixed the date to today with no backdating. The API widens that deliberately; both limits are configurable via `Attendance:*` options. |

### 1c. Testing and operations

| Decision | Why |
|---|---|
| Tests run on EF Core SQLite in-memory (`TestSupport/SqliteTestDatabase.cs`); Docker Compose runs against real SQL Server 2022 | Fast zero-dependency test run. The Docker path is the manual check for DDL drift, `datetime` precision, case-insensitive collation, and SQL Server-specific concurrency (see M4). |
| No EF migrations; `ConnectionStrings:Default` validated at startup (`ValidateOnStart`) | Schema comes from the init scripts, which are the authoritative artefact. The API throws at startup rather than running against a misconfigured or missing database. |
| `FixedTimeProvider` and `StubCurrentUser` replace real services in tests | Keeps test assertions deterministic without freezing the clock globally or leaking infrastructure into the test project. |

---

## 2. Legacy unknowns and resolutions

| Unknown | Resolution |
|---|---|
| DDL for `Schools`, `SchoolTerms`, `AttendanceSubmissionLog` (used by every procedure, defined nowhere) | Columns inferred from procedure usage; created in `db/init/010_missing_tables.sql`. **Assumption** — replace if the real DDL is found. |
| The `SchoolYear()` SQL function (called but never defined) | Replaced by `SchoolYearRule` in C# using the inline September boundary that both procedures also contain. |
| `AttendanceCodes` seed values (the form defaults to "P" only) | Seeded with P / A / E / L for the demo stack. "P" = Present is inference. |
| `sp_GetStudentsForAttendance` (called by the form, never defined) | Out of scope; nothing in the API depends on it. |
| Connection string source (`config.ini` passed as the server name in the VB form) | Replaced by `ConnectionStrings:Default` from `appsettings.json` or environment; password only in git-ignored `.env`. |
| Audit identity (`SYSTEM_USER` resolves to the shared SA login) | `ICurrentUser` writes the authenticated user name, or `"api"` until authentication is wired up (H1). Audit timestamps are written as UTC; the legacy used server-local `GETDATE()`. |
| Whether `SchoolID` belongs in row identity (the legacy lookup ignores it, the recount filters by it, the summary key omits it) | Row identity is `(StudentID, AttendDate)` with `SchoolID` stored on the row; summary keyed `(StudentID, SchoolYear)` to match `UQ_Summary`. A mid-year school transfer is an open ambiguity (see M3). |
| Whether excused absences count toward chronic status | Legacy counts every `IsAbsent = 1` row regardless of `IsExcused`. Kept as-is. |

---

## 3. Review findings (2026-11-03)

Verdict: ship after fixing H1–M3. Full report with scenarios and recommended fixes: [docs/api-review.md](docs/api-review.md).

| Severity | ID | Finding | Status |
|---|---|---|---|
| **High** | H1 | No authentication; any client that can reach the URL can read and write attendance | Open |
| Medium | M1 | Concurrent same-student saves collide on unique indexes → 500; recount can deadlock | Open |
| Medium | M2 | `null` element in `entries` → 500 instead of 400 | Open |
| Medium | M3 | Save and absenteeism count absences with different predicates; can disagree after a school transfer | Open |
| Medium | M4 | No test runs against real DDL or SQL Server; model/DDL drift is invisible to `dotnet test` | Open |
| Low | L1 | `attendDate` before 1900-09-01 → 500 instead of 400 | Open |
| Low | L2 | SA password spliced unquoted into connection string; breaks for values containing `;` | Open |
| Low | L3 | API and db-init both connect as `sa`; least-privilege login not yet created | Open |
| Low | L4 | Interrupted db-init leaves a half-built database that subsequent runs skip silently | Open |
| Low | L5 | Audit timestamps are UTC; default school year derived from UTC date, not server time zone | Open |
| Low | L6 | Host port 8080 hard-coded in compose | **Resolved** |
| Low | L7 | README did not document that inactive attendance codes are also rejected | **Resolved** |

Recommended first batch: H1, M1, M2, M3.

---

## 4. DevOps

### Azure resources

The dev environment was deployed 2026-11-03 to subscription "subscription" (pay-as-you-go), region `canadacentral`.

| Resource | Name |
|---|---|
| Resource group | `sparkrock-rg` |
| Container registry | `acrsparkrockattend7898` (Basic) |
| SQL Server | `sql-sparkrock-attendance-7898` |
| SQL Database | `Attendance` (Basic, 5 DTU) |
| Container Apps environment | `cae-sparkrock-attendance-dev` |
| Container App | `ca-attendance-api` (scales to zero; external ingress port 8080) |

Live: [https://ca-attendance-api.niceglacier-aa8a854f.canadacentral.azurecontainerapps.io](https://ca-attendance-api.niceglacier-aa8a854f.canadacentral.azurecontainerapps.io) — [Swagger](https://ca-attendance-api.niceglacier-aa8a854f.canadacentral.azurecontainerapps.io/swagger)

Ingress is restricted to the deploying host's IP (`allow-deploy-host` rule). To update it:

```bash
az containerapp ingress access-restriction set \
  -n ca-attendance-api -g sparkrock-rg \
  --rule-name allow-deploy-host --ip-address <your-ip>/32 --action Allow
# or remove the restriction entirely:
az containerapp ingress access-restriction remove \
  -n ca-attendance-api -g sparkrock-rg --rule-name allow-deploy-host
```

### Deploy or redeploy

```bash
az login
bash deploy/azure/deploy.sh   # Git Bash with Docker running; fully idempotent
```

The script clones `main`, builds the image in ACR (falls back to a local build and push if ACR Tasks are unavailable), loads the schema, updates the Container App, and smoke-tests `/health` and all three endpoints. The SQL admin password is never printed or written to disk. To rotate it:

```bash
az sql server update -g sparkrock-rg -n sql-sparkrock-attendance-7898 --admin-password <new>
# then rerun deploy.sh with SQL_ADMIN_PASSWORD=<new> exported
```

### CI/CD pipeline

`azure-pipelines.yml` builds and tests on PRs to `main`; on push to `main` it pushes the image to ACR and deploys to the Container App.

To connect it in an Azure DevOps project:
1. Create a **GitHub service connection** (GitHub App), then import the pipeline from `qadir0108/Sparkrock`.
2. Create an **Azure Resource Manager service connection** named `sc-sparkrock-attendance-dev` scoped to `sparkrock-rg`.
3. Create an **environment** named `sparkrock-attendance-dev`.

### Security note (finding H1)

The API has no authentication. Anyone who can reach the ingress URL can read and write student attendance. The database holds only sample data. `deploy.sh` restricts ingress to the deploying host's IP after smoke tests pass — keep that restriction, or switch to internal ingress, until H1 is resolved.

### Teardown

```bash
az group delete --name sparkrock-rg --yes --no-wait
```

Idle cost: approximately USD 10/month (ACR Basic ~$5, SQL Basic ~$5, Container Apps ~$0 at zero replicas, plus small Log Analytics ingestion).
