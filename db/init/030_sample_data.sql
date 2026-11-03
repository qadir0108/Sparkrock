-- Sample school, terms and students for local development and the compose verification.
-- Skipped when the db-init service runs with DB_SEED_SAMPLE_DATA=false.
INSERT INTO Schools (SchoolName, AbsenceAlertThreshold, Active)
VALUES ('Springfield Elementary', 10, 1)

DECLARE @SchoolID INT = SCOPE_IDENTITY()

INSERT INTO SchoolTerms (SchoolID, TermName, StartDate, EndDate) VALUES
    (@SchoolID, 'Fall 2025',   '2025-09-01', '2026-01-31'),
    (@SchoolID, 'Spring 2026', '2026-02-01', '2026-06-30'),
    (@SchoolID, 'Fall 2026',   '2026-09-01', '2027-01-31'),
    (@SchoolID, 'Spring 2027', '2027-02-01', '2027-06-30')

INSERT INTO Students (SchoolID, FirstName, LastName, Grade, DateOfBirth, Active) VALUES
    (@SchoolID, 'Ada',    'Lovelace', '3', '2017-12-10', 1),
    (@SchoolID, 'Alan',   'Turing',   '3', '2017-06-23', 1),
    (@SchoolID, 'Grace',  'Hopper',   '4', '2016-12-09', 1),
    (@SchoolID, 'Linus',  'Torvalds', '4', '2016-12-28', 1),
    (@SchoolID, 'Edsger', 'Dijkstra', '5', '2015-05-11', 1)
GO
