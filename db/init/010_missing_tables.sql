-- Supplementary DDL owned by the API project. Runs right after the legacy schema.sql.
--
-- 1) Tables both legacy procedures (and the form) use but schema.sql never defines. Column names and
--    types are inferred from how sp_SaveDailyAttendance.sql, sp_GetStudentAttendance.sql and
--    frmDailyAttendance.frm use them; no legacy DDL exists to confirm them.
CREATE TABLE Schools (
    SchoolID              INT          IDENTITY(1,1) PRIMARY KEY,
    SchoolName            VARCHAR(200) NOT NULL,
    AbsenceAlertThreshold INT          NULL,
    Active                BIT          NOT NULL DEFAULT 1
)

CREATE TABLE SchoolTerms (
    TermID    INT          IDENTITY(1,1) PRIMARY KEY,
    SchoolID  INT          NOT NULL,
    TermName  VARCHAR(100) NOT NULL,
    StartDate DATE         NOT NULL,
    EndDate   DATE         NOT NULL
)

CREATE TABLE AttendanceSubmissionLog (
    LogID         INT          IDENTITY(1,1) PRIMARY KEY,
    SchoolID      INT          NOT NULL,
    SubmittedDate DATETIME     NOT NULL DEFAULT GETDATE(),
    AttendDate    DATE         NOT NULL,
    RecordCount   INT          NOT NULL,
    SubmittedBy   VARCHAR(100) NULL
)
GO

-- 2) The legacy upsert assumes one StudentAttendance row per student per day; enforce it.
CREATE UNIQUE INDEX UX_StudentAttendance_StudentID_AttendDate ON StudentAttendance (StudentID, AttendDate)
GO

-- 3) Supporting indexes for the recount, alert and term lookups the save rule performs.
CREATE INDEX IX_StudentAttendance_SchoolID_AttendDate ON StudentAttendance (SchoolID, AttendDate)
CREATE INDEX IX_StudentAlerts_StudentID_SchoolYear ON StudentAlerts (StudentID, SchoolYear, AlertType)
CREATE INDEX IX_SchoolTerms_SchoolID_Dates ON SchoolTerms (SchoolID, StartDate, EndDate)
GO
