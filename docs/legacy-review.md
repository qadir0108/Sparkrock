# Sparkrock Attendance

This repository holds a legacy VB6 attendance module (`AttendanceSystem/`) and the ASP.NET Core 8 Web API that is being built to replace it (`src/`, `tests/`, `Dockerfile`, `compose.yaml`).

This README records the findings of a read-only review of the legacy module carried out on 2026-11-03, followed by the observations and assumptions that review imposed on the API work. The legacy files are the source of truth for the data model and are never modified; everything new lives outside `AttendanceSystem/`.

---

## 1. Legacy module: what it does

`AttendanceSystem` is a single-screen classic VB6 (not VB.NET) "Daily Attendance Entry" form backed by SQL Server through ADODB.

A user picks a school (and nominally a grade). The form lists that school's active students in an MSFlexGrid with an attendance code, minutes late and notes for today's date. "Save Attendance" bundles the grid into an XML string and calls `sp_SaveDailyAttendance`, which upserts one `StudentAttendance` row per student, recounts the student's absences for the school year into `StudentAttendanceSummary`, and raises a `CHRONIC_ABSENCE` alert in `StudentAlerts` when the school's threshold is reached. "Print Report" prints a Crystal Reports `.rpt` filtered to the selected school and today.

A second procedure, `sp_GetStudentAttendance`, returns a student's per-day history for a school year plus a summary row with an `IsChronicallyAbsent` flag. Nothing in the form calls it.

**As committed, the module cannot run.** The connection string is malformed, the procedure that loads the grid is not defined, the `Nz` helper is undefined, and the save procedure references a function and three tables that do not exist in the schema. Details are in sections 4 to 6.

### Files reviewed

The module is exactly these four files. A repo-wide search confirmed there is no `.vbp`, `.bas`, `.cls`, `.frx`, `.rpt` or `config.ini` anywhere in the repository.

| File | Purpose |
|---|---|
| `AttendanceSystem/Forms/frmDailyAttendance.frm` | The only form: daily attendance entry screen |
| `AttendanceSystem/Database/schema.sql` | Table definitions |
| `AttendanceSystem/Database/sp_SaveDailyAttendance.sql` | Save procedure: upsert, recount, alert |
| `AttendanceSystem/Database/sp_GetStudentAttendance.sql` | Per-student history and summary (not called by the form) |

---

## 2. Form anatomy and user workflow

### Controls (`frmDailyAttendance.frm:2-24`)

| Control | Type | Purpose |
|---|---|---|
| `frmDailyAttendance` | `VB.Form`, caption "Daily Attendance Entry", centered on screen | The only form |
| `cboSchool` | `VB.ComboBox`, TabIndex 0 | School picker; `ItemData` carries `SchoolID` |
| `cboGrade` | `VB.ComboBox`, TabIndex 1 | Grade picker; has no event handler |
| `grdStudents` | declared as `VB.MSFlexGrid`, TabIndex 3 | 6-column grid: Student ID, Last Name, First Name, Code, Min Late, Notes |
| `cmdSave` | `VB.CommandButton` "Save Attendance", TabIndex 4 | Builds XML and calls `sp_SaveDailyAttendance` |
| `cmdPrint` | `VB.CommandButton` "Print Report", TabIndex 5 | Crystal Reports print |

There is no date control and TabIndex 2 is absent. The header is not in VB6 IDE-serialized form (the IDE writes `Begin MSFlexGridLib.MSFlexGrid` with one property per line, never colon-joined properties). Inference: this `.frm` is a hand-condensed or reconstructed rendering and would not load in the VB6 IDE as-is.

### Module-level state (`frmDailyAttendance.frm:31-34`)

`moConn As ADODB.Connection`, `moRS As ADODB.Recordset`, `mlSchoolID As Long`, `mdAttendDate As Date`. All `Private`. No `.bas` globals exist; the only coupling is to the Crystal runtime and the SQL procedures.

### Event handlers and helpers, in execution order

1. **`Form_Load`** (`frmDailyAttendance.frm:35-44`). Creates `moConn` and opens it with `"Provider=SQLOLEDB;Data Source=" & App.Path & "\config.ini"` (line 38), calls `LoadSchools`, then sets `mdAttendDate = Date` (line 40). On error it shows a message box and the form stays open with a dead connection; `mdAttendDate` is left at its default because it is assigned after the connect.
2. **`LoadSchools`** (`frmDailyAttendance.frm:45-53`). Inline `SELECT SchoolID, SchoolName FROM Schools WHERE Active = 1 ORDER BY SchoolName`, fills `cboSchool`. Recordset never closed.
3. **`cboSchool_Click`** (`frmDailyAttendance.frm:54-58`). Sets `mlSchoolID`, then `LoadGrades`, then `LoadStudents`. No error handler.
4. **`LoadGrades`** (`frmDailyAttendance.frm:59-67`). Clears the combo, then inline `SELECT DISTINCT Grade FROM Students WHERE SchoolID = <id> AND Active = 1`. No error handler; recordset never closed. Because it clears the combo, `cboGrade.Text` is empty when `LoadStudents` runs next.
5. **`LoadStudents`** (`frmDailyAttendance.frm:68-100`). Builds `"EXEC sp_GetStudentsForAttendance " & mlSchoolID & ", '" & cboGrade.Text & "', '" & date & "'"` (line 72) and opens it into `moRS` without closing any previous one. Fills the grid using `Nz(..., "P")`, `Nz(..., "0")`, `Nz(..., "")` (lines 90-92). `Nz` is an Access/VBA function, not VB6, and is defined nowhere in the repo; under `Option Explicit` (line 30) this is a compile error.
6. **Missing `cboGrade_Click`.** Changing grade does nothing. `LoadStudents` only runs from `cboSchool_Click`, immediately after `LoadGrades` emptied the combo, so the grade argument is always empty.
7. **Grid editing.** MSFlexGrid has no in-place cell editing and there are no `grdStudents_*` handlers or overlay TextBox. As written the user cannot change Code, Min Late or Notes before saving.
8. **`cmdSave_Click`** (`frmDailyAttendance.frm:101-122`). Loops grid rows, builds `<attendance><r sid=".." code=".." min=".." note=".."/>...</attendance>` by string concatenation, defaulting an empty code to `"P"` (lines 105-115), then runs `EXEC sp_SaveDailyAttendance <id>, '<date>', '<xml>'` (lines 116-117). No check that a school is selected; no transaction; no XML or SQL escaping.
9. **`cmdPrint_Click`** (`frmDailyAttendance.frm:123-132`). Late-bound `CreateObject("CrystalRuntime.Application")`, opens `App.Path\Reports\DailyAttendance.rpt`, sets a selection formula on `{Attendance.SchoolID}` and `{Attendance.AttendDate}`, and prints without a dialog. No error handler.
10. **`Form_Unload`** (`frmDailyAttendance.frm:133-139`). Closes `moConn` if open; sets `moRS` to `Nothing` without closing it.

Net workflow: open form, pick school (grid loads for grade ""), optionally pick grade (no effect), Save, Print. The attendance date is always today with no way to back-date.

---

## 3. Data flow

### Connection string

Hardcoded at `frmDailyAttendance.frm:38`: `Provider=SQLOLEDB;Data Source=<App.Path>\config.ini`. No `Initial Catalog`, no credentials. The file `config.ini` is never opened; its path is passed as the server name. Inference: the author intended to read a connection string from `config.ini`, but the code tells SQLOLEDB to connect to a server literally named after the file path. No registry or other config source exists.

### Tables and columns touched from VB

All access is inline SQL through `moConn.Execute` or `Recordset.Open`. Nothing uses `adCmdStoredProc` or `Parameters.Append`.

| Object | Columns | Op | Where | In `schema.sql`? |
|---|---|---|---|---|
| `Schools` | `SchoolID, SchoolName, Active` | read | `frmDailyAttendance.frm:47` | **No** |
| `Students` | `Grade, SchoolID, Active` | read | `frmDailyAttendance.frm:62` | Yes (`schema.sql:1-10`) |
| result of `sp_GetStudentsForAttendance` | `StudentID, LastName, FirstName, AttendCode, MinutesLate, Notes` | read | `frmDailyAttendance.frm:87-92` | procedure undefined |
| Crystal `{Attendance.SchoolID}`, `{Attendance.AttendDate}` | | report | `frmDailyAttendance.frm:128-130` | **No table named `Attendance`**; schema has `StudentAttendance` |

### Tables touched by `sp_SaveDailyAttendance`

| Object | Op | Lines | In `schema.sql`? |
|---|---|---|---|
| `SchoolTerms(TermID, SchoolID, StartDate, EndDate)` | read | 36-39 | **No** |
| `AttendanceCodes(CodeValue, IsAbsent, IsExcused)` | read | 46-48 | Yes |
| `StudentAttendance` | read/update/insert | 54-77, 79-84 | Yes |
| `StudentAttendanceSummary(StudentID, SchoolID, SchoolYear, TotalAbsences, LastUpdated)` | upsert | 85-96 | Yes |
| `Schools(SchoolID, AbsenceAlertThreshold)` | read | 98 | **No** |
| `StudentAlerts(StudentID, SchoolID, AlertType, SchoolYear, AlertDate, AlertMessage, ResolvedDate)` | read/insert | 101-114 | Yes |
| `AttendanceSubmissionLog(SchoolID, SubmittedDate, AttendDate, RecordCount, SubmittedBy)` | insert | 120-121 | **No** |
| `SchoolYear(@AttendDate)` scalar function | call | 83 | **No**, and not schema-qualified |

`sp_GetStudentAttendance` touches `StudentAttendance`, `AttendanceCodes`, `StudentAttendanceSummary` and `Students` (all in schema), plus `SchoolTerms(TermID, TermName)` (line 28) and `Schools(SchoolID, AbsenceAlertThreshold)` (line 43), which are **not in schema**.

### Stored procedures called from VB versus defined

| Called from VB | Arguments passed | Defined? | Match |
|---|---|---|---|
| `sp_GetStudentsForAttendance` (`frmDailyAttendance.frm:72`) | `<SchoolID>, '<Grade>', '<yyyy-mm-dd>'` | **No file defines it.** Only `sp_GetStudentAttendance` exists, with a different name, signature (`@StudentID INT, @SchoolYear VARCHAR(9) = NULL`) and purpose. | **Procedure called but not defined.** The signatures are incompatible, so it is not the same routine. |
| `sp_SaveDailyAttendance` (`frmDailyAttendance.frm:116-117`) | `<SchoolID>, '<yyyy-mm-dd>', '<xml>'` | Yes (`sp_SaveDailyAttendance.sql:1-5`: `@SchoolID INT, @AttendDate DATE, @AttendanceXML NVARCHAR(MAX)`) | Count and order match. The XML literal is not `N`-prefixed, so non-ASCII notes would be mangled. Inline string EXEC, not parameterized. |
| `sp_GetStudentAttendance` | never called | Yes | Orphan within this repo. |

`mlSchoolID` is a `Long`, so numeric concatenations are not injectable. `cboGrade.Text` (an editable DropDown by default) and the grid's Code and Notes cells are free text concatenated into both the XML body and the surrounding SQL literal.

---

## 4. What each stored procedure does

### `sp_SaveDailyAttendance`

1. `SET NOCOUNT ON` (line 7). No `BEGIN TRAN`, no `TRY/CATCH`, no `RAISERROR`, no `RETURN` value, no result set. Each statement autocommits individually.
2. Shreds `@AttendanceXML` with `CAST(... AS XML)` and `.nodes('/attendance/r')`, reading `@sid INT, @code VARCHAR(5), @min INT, @note VARCHAR(500)` into a table variable (lines 18-31). No validation; malformed XML raises a conversion error to the caller.
3. Derives `@SchoolYear` from `@AttendDate`: month >= 9 gives `'YYYY-YYYY+1'`, otherwise `'YYYY-1-YYYY'` (lines 32-35). September start is hardcoded.
4. Looks up `@TermID` from `SchoolTerms` where the date falls within the term for the school (lines 36-39). NULL if none; arbitrary if terms overlap.
5. Opens a `LOCAL FAST_FORWARD` cursor over the table variable (lines 40-43) and per row:
   - Reads `IsAbsent` and `IsExcused` from `AttendanceCodes` by `CodeValue`; if `@IsAbsent IS NULL` sets both to 0 (lines 46-53). Unknown codes are accepted silently. **Bug:** the variables are never reset between iterations. A `SELECT @v = ...` that matches zero rows leaves `@v` unchanged, so the NULL check only works on the first iteration. An unknown code following an absent code inherits "absent".
   - Reads `@ExistingID` from `StudentAttendance` by `StudentID` and `AttendDate`, not by `SchoolID` (lines 54-56). **Bug, same cause:** `@ExistingID` is never reset to NULL. Once any student in the batch has an existing row, every later student without one takes the UPDATE branch (lines 57-68) and overwrites the previous student's row with its own values. No row is inserted for it. This is cross-student data corruption on any re-save of a partially entered day.
   - UPDATE sets `ModifiedDate = GETDATE(), ModifiedBy = SYSTEM_USER` (lines 59-67); INSERT sets `TermID`, `CreatedDate`, `CreatedBy = SYSTEM_USER` (lines 71-76).
   - Recounts absences with `WHERE StudentID = @StudentID AND SchoolID = @SchoolID AND SchoolYear(@AttendDate) = @SchoolYear AND IsAbsent = 1` (lines 79-84). Two problems. First, `SchoolYear` is not a SQL Server built-in and no user function is defined; an unqualified unknown name yields error 195, which is normally raised when the procedure is created, so the script likely cannot be deployed at all. Second, even if `dbo.SchoolYear` existed, it is applied to the parameter rather than the row's `AttendDate`, so the predicate is a constant and the count becomes the student's lifetime absences at that school, inflating chronic-absence alerts.
   - Upserts `StudentAttendanceSummary` keyed on `(StudentID, SchoolYear)` (lines 85-96), consistent with `UQ_Summary` (`schema.sql:45`) but not with the per-`SchoolID` recount.
   - Reads `@Threshold = ISNULL(AbsenceAlertThreshold, 10)` from `Schools` (lines 97-98). If the school row is missing, `@Threshold` stays NULL and no alert can ever fire. If `@TotalAbsences >= @Threshold` and no unresolved `'CHRONIC_ABSENCE'` alert exists for that student and year, inserts one into `StudentAlerts` (lines 99-115).
6. Closes and deallocates the cursor (lines 118-119).
7. Inserts a row into `AttendanceSubmissionLog` with the record count and `SubmittedBy = SYSTEM_USER` (lines 120-121). Because this table is not in `schema.sql`, every save would raise here after all attendance rows had already been autocommitted. The VB form would then show "Save failed" for a save that mostly happened.

Feature requirements: XML type and `NVARCHAR(MAX)` need SQL Server 2005 or later; `DATE` needs 2008 or later.

### `sp_GetStudentAttendance`

- Parameters `@StudentID INT, @SchoolYear VARCHAR(9) = NULL` (lines 2-3). If `@SchoolYear` is NULL, derives the current school year with the same September rule (lines 8-16).
- Result set 1 (lines 17-35): all `StudentAttendance` rows for the student whose computed school year equals `@Year` (inline `CASE` on `sa.AttendDate`, not index-friendly), `INNER JOIN AttendanceCodes` for `Description` (rows whose code is not in `AttendanceCodes` are silently dropped, and the save procedure allows such codes in), `LEFT JOIN SchoolTerms` for `TermName`, ordered by date descending.
- Result set 2 (lines 36-45): the `StudentAttendanceSummary` row for that year joined to `Students` and `Schools`, returning `TotalAbsences, LastUpdated, AbsenceAlertThreshold` and an `IsChronicallyAbsent` flag using the same `ISNULL(threshold, 10)` rule.
- Read-only; no transaction, no error handling, no `RETURN`. Two result sets, so an ADO caller would need `NextRecordset`.

---

## 5. External dependencies

- **ADODB** (early-bound `ADODB.Connection` and `ADODB.Recordset`, constants `adOpenStatic`, `adLockOptimistic`, `adStateOpen`) at `frmDailyAttendance.frm:31-32, 74, 135`. Needs a project reference; version unknown because there is no `.vbp`.
- **SQLOLEDB** OLE DB provider at `frmDailyAttendance.frm:38`. Deprecated by Microsoft and predates the `DATE` type used in the schema, which it surfaces as strings.
- **MSFlexGrid** (`MSFLXGRD.OCX`) at `frmDailyAttendance.frm:15-17, 75-96, 106-113`. Declared as `VB.MSFlexGrid` rather than `MSFlexGridLib.MSFlexGrid`.
- **Crystal Reports RDC runtime** via late-bound `CreateObject("CrystalRuntime.Application")` at `frmDailyAttendance.frm:125` (`craxdrt.dll`, Crystal 8.5 to XI era, 32-bit only, discontinued). The report file `App.Path\Reports\DailyAttendance.rpt` is not in the repo.
- **File system**: `App.Path & "\config.ini"` (path only, never read) and `App.Path & "\Reports\DailyAttendance.rpt"`. No file I/O code.
- **Win32 `Declare`**: none. **Registry**: none. **`On Error Resume Next`**: none.
- **Undefined symbol**: `Nz` at `frmDailyAttendance.frm:90-92`. Must come from a `.bas` that is not in the repo.
- **SQL Server side**: `SYSTEM_USER` for audit columns; XML shredding; cursor; objects `Schools`, `SchoolTerms`, `AttendanceSubmissionLog`, function `SchoolYear` and procedure `sp_GetStudentsForAttendance` are all expected to exist outside `schema.sql`.

---

## 6. Risks and smells

### High

1. **Malformed, hardcoded connection string.** `Data Source` is a `.ini` file path; no database or credentials; `config.ini` is never parsed and is absent. The form cannot connect. `frmDailyAttendance.frm:38`.
2. **`sp_GetStudentsForAttendance` is called but defined nowhere.** The only similar procedure has an incompatible signature. The grid can never load. `frmDailyAttendance.frm:72`.
3. **SQL and XML injection.** Free-text grade and grid Code and Notes cells are concatenated into `EXEC` strings and hand-built XML with no escaping. A `'` in a note breaks the SQL; `"`, `<` or `&` break the XML cast. `frmDailyAttendance.frm:72, 108-117`.
4. **Stale cursor-loop variables in `sp_SaveDailyAttendance`.** `@ExistingID` (lines 54-57) is never reset, so later students overwrite earlier students' rows. `@IsAbsent` and `@IsExcused` (lines 46-53) are never reset, so unknown codes inherit the previous row's flags.
5. **`SchoolYear(@AttendDate)` is undefined** at `sp_SaveDailyAttendance.sql:83`. Deployment or runtime error 195, and logically a no-op filter even if it existed.
6. **No transaction anywhere.** A failure mid-cursor or at the final log insert leaves partial autocommitted writes while the UI reports failure. `sp_SaveDailyAttendance.sql:7-122`, `frmDailyAttendance.frm:116-121`.
7. **Three tables missing from `schema.sql`.** `Schools`, `SchoolTerms`, `AttendanceSubmissionLog`. The last guarantees every save ends in an error after writing.
8. **`Nz` undefined under `Option Explicit`.** Compile failure unless supplied by a missing module. `frmDailyAttendance.frm:30, 90-92`.
9. **The grid is not editable.** No cell-editing code exists, so the user cannot change values before saving. `frmDailyAttendance.frm:75-96, 101-115`.

### Medium

10. No `cboGrade_Click`; grade selection never reloads the grid. `frmDailyAttendance.frm:54-67`.
11. Attendance date fixed to today with no UI to change it. `frmDailyAttendance.frm:40`.
12. Missing error handlers in `cboSchool_Click`, `LoadGrades`, `cmdPrint_Click`; `Form_Load` swallows connect failure and continues with a dead connection. `frmDailyAttendance.frm:54, 59, 123, 42-43`.
13. Recordsets never closed in `LoadSchools`, `LoadGrades`, `LoadStudents`, `Form_Unload`. `frmDailyAttendance.frm:47, 62, 73, 138`.
14. Crystal selection formula targets a table `Attendance` that does not exist in the schema; prints with no prompt; no handler for a missing runtime. `frmDailyAttendance.frm:125-131`.
15. `SYSTEM_USER` as audit identity records the SQL login, not the application user. `sp_SaveDailyAttendance.sql:66, 76, 121`.
16. Schema has no foreign keys, no unique constraint on `StudentAttendance(StudentID, AttendDate)`, and no index supporting the per-row lookups inside the cursor. `schema.sql:21-36`.
17. Unknown attendance codes are accepted on save and then hidden from history by the `INNER JOIN AttendanceCodes`. A missing `Schools` row silently disables alerts.
18. `.frm` header is not IDE-serialized; it may be a condensed copy rather than the real source. `frmDailyAttendance.frm:9-23`.
19. Existing-row lookup ignores `SchoolID` while the recount filters by it and the summary key omits it; transfer students behave inconsistently.

### Low

20. `cmdSave` does not guard against no school selected; sends `sid=""`, which fails the `INT` cast. `frmDailyAttendance.frm:101-117`.
21. XML literal not `N'...'`-prefixed into `NVARCHAR(MAX)`; non-ASCII notes mangled. `frmDailyAttendance.frm:116-117`.
22. `Val()` can yield a non-integer that fails the `INT` cast. `frmDailyAttendance.frm:112`.
23. Overlapping `SchoolTerms` give an arbitrary `@TermID`. `sp_SaveDailyAttendance.sql:36-39`.
24. Success message is unconditional; the procedure returns nothing to confirm row counts. `frmDailyAttendance.frm:118`.
25. September school-year boundary hardcoded in both procedures.
26. `sp_GetStudentAttendance` is dead code relative to this form.

---

## 7. Open questions

These are unknowns the four files do not answer. They are recorded as unknowns, not guesses.

- No `.vbp`: startup object, other forms and modules, exact ADO, MSFlexGrid and Crystal reference versions, and whether `frmDailyAttendance` is the startup form.
- Where `Nz` is defined.
- The intended format and contents of `config.ini`, and how the real connection string was meant to be loaded.
- The definition of `sp_GetStudentsForAttendance`: its parameters, its result columns, and how it treats an empty grade.
- DDL for `Schools`, `SchoolTerms` and `AttendanceSubmissionLog`.
- Whether a `dbo.SchoolYear` scalar function was ever intended to exist, and its definition.
- Contents of `Reports\DailyAttendance.rpt` and whether it aliases `StudentAttendance` as `Attendance`.
- Who, if anyone, calls `sp_GetStudentAttendance`.
- Seed data for `AttendanceCodes`. The code `"P"` defaulted by the form is presumably "Present" (inference).
- Target SQL Server version. Schema and procedures imply 2008 or later; the SQLOLEDB provider is from an earlier era.
- Whether the `.frm` in the repo is the actual IDE-saved source or a condensed rendering.

---

## 8. Effect on the API work: observations and assumptions

The new ASP.NET Core 8 API exposes three endpoints: save daily attendance for a batch of students, get a student's attendance history, and get a student's chronic absenteeism status. The legacy findings above changed how those endpoints are built. Each item below is an observation drawn from the legacy code, followed by the assumption the API makes because of it. Revisit these assumptions if any of the open questions in section 7 are answered.

### Observation: the legacy save procedure cannot be deployed or run

`sp_SaveDailyAttendance` references an undefined function (`SchoolYear`) and three tables absent from the schema, has no transaction, and contains a cursor bug that corrupts other students' rows on re-save.

**Assumption:** the API does not call `sp_SaveDailyAttendance`. It implements the equivalent business rule in EF Core inside a single transaction so the whole batch succeeds or fails together. The rule preserved from the legacy code is:

1. Upsert one `StudentAttendance` row per `(StudentID, AttendDate)` with `SchoolID`, `AttendCode`, `MinutesLate`, `Notes`, `IsAbsent` and `IsExcused` looked up from `AttendanceCodes`, `TermID` from `SchoolTerms` when a term covers the date, `SchoolYear` computed with the September rule, and the audit columns.
2. Recount the student's `IsAbsent = 1` rows for that school year and upsert `StudentAttendanceSummary(StudentID, SchoolYear)`.
3. If `TotalAbsences >= ISNULL(Schools.AbsenceAlertThreshold, 10)` and no unresolved `CHRONIC_ABSENCE` alert exists for that student and year, insert one into `StudentAlerts`.
4. Insert an `AttendanceSubmissionLog` row.

Bugs deliberately not carried over: the stale `@ExistingID` overwrite, the stale `@IsAbsent`/`@IsExcused` inheritance, the lifetime absence count caused by the misapplied `SchoolYear` filter, and silent acceptance of unknown codes. The API rejects unknown attendance codes with a 400 instead.

### Observation: the schema is incomplete

`Schools`, `SchoolTerms` and `AttendanceSubmissionLog` are used but never defined, and `StudentAttendance` has no unique constraint on student plus date.

**Assumption:** the Docker `db-init` step runs the legacy `schema.sql` first, then a supplementary script owned by the API project (outside `AttendanceSystem/`) that creates the three missing tables, adds a unique index on `StudentAttendance(StudentID, AttendDate)`, and seeds `AttendanceCodes` plus a sample school and students. The column lists for the missing tables are inferred from how the procedures use them and are not confirmed by any legacy DDL. The legacy `sp_*.sql` files run last; if `sp_SaveDailyAttendance` fails to create because of the undefined function, that step is non-fatal and the legacy file is left untouched.

### Observation: the history procedure is deployable but has quirks

`sp_GetStudentAttendance` works once `Schools` and `SchoolTerms` exist. It returns two result sets, orders by date descending, and its `INNER JOIN AttendanceCodes` drops rows with unknown codes.

**Assumption:** the history endpoint reproduces result set 1 either by calling the procedure or by an equivalent EF Core query, documented in the API report. Because the API rejects unknown codes on save, no rows are lost to the inner join for data written through the API. Rows written by other means with unknown codes would still be hidden, matching legacy behaviour.

### Observation: the legacy code defines chronic absenteeism as a count, not a percentage

Both procedures encode the same rule: a student is chronically absent when the school year's `TotalAbsences` (count of `IsAbsent = 1` rows) is greater than or equal to the school's `AbsenceAlertThreshold`, defaulting to 10 when the school has no threshold (`sp_GetStudentAttendance.sql:36-45`, `sp_SaveDailyAttendance.sql:97-115`).

**Assumption:** the absenteeism endpoint honours this count-based, per-school-year rule rather than a percentage-of-days rule. It takes an optional school year (defaulting to the current year by the September rule) and returns total days recorded, days absent, the threshold applied, whether the student is chronically absent, and whether an unresolved `CHRONIC_ABSENCE` alert exists. A configurable fallback threshold of 10 is used only when the school row has no threshold.

### Observation: the school-year boundary is hardcoded to September

**Assumption:** the API keeps the September rule (`'YYYY-YYYY+1'` for months 9 to 12, `'YYYY-1-YYYY'` otherwise) so that its school-year strings match any data the legacy procedures might have written. The boundary is not configurable.

### Observation: the legacy UI always saves for today, for one school, with no way to back-date

**Assumption:** the API accepts an explicit attendance date and school in the save request rather than fixing the date to today. This is a deliberate widening of the legacy behaviour.

### Observation: audit identity is the SQL login

`CreatedBy`, `ModifiedBy` and `SubmittedBy` are set to `SYSTEM_USER`, which is the shared SQL login.

**Assumption:** the API writes the authenticated user name into these columns where one is available (`ICurrentUser`) and otherwise the constant `"api"` until authentication is added, preserving the column shapes.

### Not addressed by the API

The API does not replace the Crystal Reports print, the undefined `sp_GetStudentsForAttendance` grid loader, or the `config.ini` connection mechanism. Those remain open questions for the legacy side.
