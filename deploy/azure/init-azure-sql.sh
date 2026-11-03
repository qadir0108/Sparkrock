#!/bin/bash
# Applies the Attendance schema to an existing Azure SQL database (or any SQL Server database that
# already exists). Azure SQL counterpart of db/init/init-db.sh, which cannot be used there because it
# runs CREATE DATABASE, connects as sa and exits early when the database exists.
#
# Usage:   SQLCMDPASSWORD=... init-azure-sql.sh <server> <database> <user>
#          <server> is the host name, e.g. sql-x.database.windows.net (tcp: and ,1433 are added).
# Inputs:  LEGACY_DIR (default /legacy)   -> AttendanceSystem/Database, mounted read-only
#          INIT_DIR   (default /db-init)  -> db/init, mounted read-only
#          DB_SEED_SAMPLE_DATA (default true)
# The password is read by sqlcmd from SQLCMDPASSWORD and never appears on a command line.
#
# Order: schema.sql -> 010_missing_tables.sql -> 020_reference_data.sql -> every legacy sp_*.sql
#        (sp_SaveDailyAttendance.sql is non-fatal, expected error 195) -> 030_sample_data.sql
#
# Idempotent: exits 0 without changes when dbo.StudentAttendance already exists. A failed run leaves a
# partly built database; delete it (az sql db delete) and re-run deploy/azure/deploy.sh.
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: SQLCMDPASSWORD=... $0 <server> <database> <user>" >&2
  exit 2
fi
SERVER="$1"
DB_NAME="$2"
DB_USER="$3"
: "${SQLCMDPASSWORD:?SQLCMDPASSWORD must be set}"

SQLCMD="${SQLCMD:-/opt/mssql-tools18/bin/sqlcmd}"
LEGACY_DIR="${LEGACY_DIR:-/legacy}"
INIT_DIR="${INIT_DIR:-/db-init}"
SEED_SAMPLE_DATA="${DB_SEED_SAMPLE_DATA:-true}"
NON_FATAL_SCRIPTS="${NON_FATAL_SCRIPTS:-sp_SaveDailyAttendance.sql}"
# Local rehearsal only (self-signed container cert): SQLCMD_TRUST_SERVER_CERT=true adds -C. Never for Azure SQL.
TRUST_ARGS=(); [ "${SQLCMD_TRUST_SERVER_CERT:-false}" = "true" ] && TRUST_ARGS=(-C)

# Azure SQL presents a publicly trusted certificate, so encryption is mandatory and the
# certificate is validated (no -C / TrustServerCertificate).
sql() {
  "$SQLCMD" -S "tcp:${SERVER},1433" -d "$DB_NAME" -U "$DB_USER" -N "${TRUST_ARGS[@]}" -l 30 -b "$@"
}

echo "init-azure-sql: waiting for $SERVER/$DB_NAME"
for attempt in $(seq 1 20); do
  if sql -Q "SELECT 1" >/dev/null 2>&1; then
    break
  fi
  if [ "$attempt" -eq 20 ]; then
    echo "init-azure-sql: cannot connect to $SERVER/$DB_NAME (firewall rule, credentials or database state)" >&2
    sql -Q "SELECT 1" || true
    exit 1
  fi
  sleep 6
done

exists=$(sql -h -1 -W -Q "SET NOCOUNT ON; SELECT CASE WHEN OBJECT_ID(N'dbo.StudentAttendance', N'U') IS NULL THEN 0 ELSE 1 END" | tr -d '[:space:]')
if [ "$exists" = "1" ]; then
  echo "init-azure-sql: schema already present in $DB_NAME; nothing to do"
  exit 0
fi

run_script() {
  local path="$1"
  local name
  name=$(basename "$path")
  echo "init-azure-sql: running $name"
  if sql -i "$path"; then
    return 0
  fi
  if [ "${2:-}" = "nonfatal" ]; then
    echo "init-azure-sql: WARNING: $name failed and was skipped (known legacy defect; see docs/legacy-review.md section 4)" >&2
    return 0
  fi
  echo "init-azure-sql: $name failed; database $DB_NAME is partly initialised" >&2
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

echo "init-azure-sql: done"
