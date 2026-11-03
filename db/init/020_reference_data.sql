-- Attendance codes. The legacy scripts never seed AttendanceCodes; the only code evidenced by the
-- legacy code is "P", which the form defaults to (frmDailyAttendance.frm:90, 109). The remaining codes
-- are an assumption: replace this list with the production one before go-live.
INSERT INTO AttendanceCodes (CodeValue, Description, IsAbsent, IsExcused, IsActive) VALUES
    ('P', 'Present',            0, 0, 1),
    ('A', 'Absent (unexcused)', 1, 0, 1),
    ('E', 'Absent (excused)',   1, 1, 1),
    ('L', 'Late',               0, 0, 1)
GO
