#!/bin/bash
# One-shot database initialisation for the compose stack (service: db-init).
# Runs the legacy scripts under AttendanceSystem/Database as-is, plus the supplementary scripts in this
# folder that the legacy scripts depend on. The legacy files are never edited.
#
# Order: CREATE DATABASE -> legacy schema.sql -> 010_missing_tables.sql -> 020_reference_data.sql
#        -> every legacy sp_*.sql -> 030_sample_data.sql (unless DB_SEED_SAMPLE_DATA=false)
#
# Idempotent: exits 0 without touching anything when the database already exists. If any step fails,
# the half-built database is dropped so the next run starts clean.
set -euo pipefail

SQLCMD=/opt/mssql-tools18/bin/sqlcmd
SERVER="${DB_SERVER:-sqlserver}"
DB_NAME="${DB_NAME:-Attendance}"
LEGACY_DIR="${LEGACY_DIR:-/legacy}"
INIT_DIR="${INIT_DIR:-/db-init}"
SEED_SAMPLE_DATA="${DB_SEED_SAMPLE_DATA:-true}"
# Legacy scripts allowed to fail. sp_SaveDailyAttendance.sql:83 calls SchoolYear(@AttendDate), which is not a
# SQL Server built-in and is not schema-qualified, so CREATE PROCEDURE fails with error 195. The API
# implements that procedure's rule in code (src/Attendance.Api/Services/DailyAttendanceService.cs).
NON_FATAL_SCRIPTS="${NON_FATAL_SCRIPTS:-sp_SaveDailyAttendance.sql}"

: "${MSSQL_SA_PASSWORD:?MSSQL_SA_PASSWORD must be set}"

sql() {
  "$SQLCMD" -S "$SERVER" -U sa -P "$MSSQL_SA_PASSWORD" -C -b "$@"
}

echo "db-init: waiting for SQL Server at $SERVER"
for attempt in $(seq 1 30); do
  if sql -Q "SELECT 1" >/dev/null 2>&1; then
    break
  fi
  if [ "$attempt" -eq 30 ]; then
    echo "db-init: SQL Server did not become ready" >&2
    exit 1
  fi
  sleep 2
done

exists=$(sql -h -1 -W -v DbName="$DB_NAME" -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name = N'\$(DbName)'" | tr -d '[:space:]')
if [ "$exists" = "1" ]; then
  echo "db-init: database $DB_NAME already exists; nothing to do"
  exit 0
fi

cleanup_on_error() {
  echo "db-init: initialisation failed; dropping $DB_NAME so the next run starts clean" >&2
  sql -v DbName="$DB_NAME" -Q "IF DB_ID(N'\$(DbName)') IS NOT NULL BEGIN ALTER DATABASE [\$(DbName)] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [\$(DbName)]; END" || true
}
trap cleanup_on_error ERR

echo "db-init: creating database $DB_NAME"
sql -v DbName="$DB_NAME" -Q "CREATE DATABASE [\$(DbName)]"

# run_script <path> [nonfatal]
run_script() {
  local path="$1"
  local name
  name=$(basename "$path")
  echo "db-init: running $name"
  if sql -d "$DB_NAME" -i "$path"; then
    return 0
  fi
  if [ "${2:-}" = "nonfatal" ]; then
    echo "db-init: WARNING: $name failed and was skipped (known legacy defect; see docs/legacy-review.md section 4)" >&2
    return 0
  fi
  return 1
}

run_script "$LEGACY_DIR/schema.sql"
run_script "$INIT_DIR/010_missing_tables.sql"
run_script "$INIT_DIR/020_reference_data.sql"

for script in "$LEGACY_DIR"/sp_*.sql; do
  name=$(basename "$script")
  case " $NON_FATAL_SCRIPTS " in
    *" $name "*) run_script "$script" nonfatal ;;
    *)           run_script "$script" ;;
  esac
done

if [ "$SEED_SAMPLE_DATA" = "true" ]; then
  run_script "$INIT_DIR/030_sample_data.sql"
fi

echo "db-init: done"
