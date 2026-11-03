#!/bin/bash
# Deploys the Attendance API to Azure Container Apps with an Azure SQL database.
# Idempotent: every resource is created only when missing, so re-running redeploys the latest commit.
#
#   az login && az account set --subscription <id>
#   bash deploy/azure/deploy.sh
#
# Requires: Azure CLI, git, curl, openssl, and Docker (sqlcmd runs from the SQL Server image).
# Secrets: the SQL admin password is taken from SQL_ADMIN_PASSWORD if set, else recovered from the
# container app's existing secret, else generated. It is never printed or written to disk.
set -euo pipefail
export MSYS_NO_PATHCONV=1   # Git Bash on Windows: keep /subscriptions/... and /scripts paths intact

# ---- parameters --------------------------------------------------------------------------------
SUBSCRIPTION_ID="${SUBSCRIPTION_ID:-f8b1dfb3-7048-4523-9a9e-992da085ae35}"
RESOURCE_GROUP="${RESOURCE_GROUP:-sparkrock-rg}"
LOCATION="${LOCATION:-canadacentral}"          # chosen by the user
SQL_LOCATION="${SQL_LOCATION:-$LOCATION}"       # override if the region refuses SQL provisioning
GIT_URL="${GIT_URL:-https://github.com/qadir0108/Sparkrock.git}"
GIT_REF="${GIT_REF:-main}"
SOURCE_DIR="${SOURCE_DIR:-}"                    # existing clone to build; empty = fresh clone of GIT_REF
# Deterministic 4-char suffix so re-runs find the same globally unique names.
SUFFIX="${SUFFIX:-$(printf '%s' "$SUBSCRIPTION_ID/$RESOURCE_GROUP" | sha1sum | cut -c1-4)}"
ACR_NAME="${ACR_NAME:-acrsparkrockattend$SUFFIX}"
SQL_SERVER="${SQL_SERVER:-sql-sparkrock-attendance-$SUFFIX}"
SQL_DB="${SQL_DB:-Attendance}"
SQL_ADMIN="${SQL_ADMIN:-attendanceadmin}"
CAE_NAME="${CAE_NAME:-cae-sparkrock-attendance-dev}"
APP_NAME="${APP_NAME:-ca-attendance-api}"
IMAGE_REPO="${IMAGE_REPO:-attendance-api}"
MIN_REPLICAS="${MIN_REPLICAS:-0}"               # 0 = scale to zero when idle (cold start on first call)
MAX_REPLICAS="${MAX_REPLICAS:-1}"
RESTRICT_INGRESS="${RESTRICT_INGRESS:-true}"    # after smoke tests pass, allow only this host's IP
MSSQL_IMAGE="mcr.microsoft.com/mssql/server:2022-latest"

log() { printf '\n==> %s\n' "$*"; }

az account set --subscription "$SUBSCRIPTION_ID"
az config set extension.use_dynamic_install=yes_without_prompt --only-show-errors >/dev/null
az extension add --name containerapp --upgrade --only-show-errors >/dev/null

for ns in Microsoft.App Microsoft.OperationalInsights Microsoft.ContainerRegistry Microsoft.Sql; do
  az provider register --namespace "$ns" --wait --only-show-errors >/dev/null
done

# ---- source ------------------------------------------------------------------------------------
WORK_DIR="$(mktemp -d)"
command -v cygpath >/dev/null 2>&1 && WORK_DIR="$(cygpath -m -a "$WORK_DIR")"  # Git Bash: MSYS_NO_PATHCONV is on, so give git/az/docker a path they resolve like the shell does
HOST_IP_RULE="deploy-host"
cleanup() {
  az sql server firewall-rule delete -g "$RESOURCE_GROUP" -s "$SQL_SERVER" -n "$HOST_IP_RULE" \
    --only-show-errors >/dev/null 2>&1 || true
  rm -rf "$WORK_DIR"
}
trap cleanup EXIT

if [ -z "$SOURCE_DIR" ]; then
  log "Cloning $GIT_URL ($GIT_REF)"
  git clone --quiet --depth 1 --branch "$GIT_REF" "$GIT_URL" "$WORK_DIR/src"
  SOURCE_DIR="$WORK_DIR/src"
fi
COMMIT_SHA="$(git -C "$SOURCE_DIR" rev-parse HEAD)"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
log "Deploying commit $COMMIT_SHA"

# ---- resource group and registry ---------------------------------------------------------------
log "Resource group $RESOURCE_GROUP ($LOCATION)"
az group create -n "$RESOURCE_GROUP" -l "$LOCATION" --only-show-errors -o none

if ! az acr show -n "$ACR_NAME" -g "$RESOURCE_GROUP" -o none 2>/dev/null; then
  log "Creating container registry $ACR_NAME"
  az acr create -n "$ACR_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION" --sku Basic --admin-enabled false \
    --only-show-errors -o none
fi
ACR_SERVER="$(az acr show -n "$ACR_NAME" --query loginServer -o tsv)"
ACR_ID="$(az acr show -n "$ACR_NAME" --query id -o tsv)"
IMAGE="$ACR_SERVER/$IMAGE_REPO:$COMMIT_SHA"

log "Building $IMAGE (target final)"
if ! az acr build -r "$ACR_NAME" -f "$SOURCE_DIR/Dockerfile" --target final \
      -t "$IMAGE_REPO:$COMMIT_SHA" -t "$IMAGE_REPO:latest" "$SOURCE_DIR" --only-show-errors; then
  # Some subscriptions cannot run ACR Tasks; fall back to a local build and push.
  log "az acr build failed; falling back to local docker build and push"
  az acr login -n "$ACR_NAME"
  docker build --target final -t "$IMAGE" -t "$ACR_SERVER/$IMAGE_REPO:latest" "$SOURCE_DIR"
  docker push "$IMAGE"
  docker push "$ACR_SERVER/$IMAGE_REPO:latest"
fi

# ---- admin password ----------------------------------------------------------------------------
PASSWORD_SOURCE="env"
if [ -z "${SQL_ADMIN_PASSWORD:-}" ] && az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" -o none 2>/dev/null; then
  existing_conn="$(az containerapp secret show -n "$APP_NAME" -g "$RESOURCE_GROUP" --secret-name sqlconn \
                   --query value -o tsv 2>/dev/null || true)"
  SQL_ADMIN_PASSWORD="$(printf '%s' "$existing_conn" | sed -n 's/.*;Password=\([^;]*\);.*/\1/p')"
  PASSWORD_SOURCE="existing"
fi
if [ -z "${SQL_ADMIN_PASSWORD:-}" ]; then
  SQL_ADMIN_PASSWORD="$(openssl rand -base64 30 | tr -dc 'A-Za-z0-9' | cut -c1-24)Aa1_"
  PASSWORD_SOURCE="generated"
fi

# ---- Azure SQL ---------------------------------------------------------------------------------
if ! az sql server show -n "$SQL_SERVER" -g "$RESOURCE_GROUP" -o none 2>/dev/null; then
  log "Creating SQL server $SQL_SERVER ($SQL_LOCATION)"
  az sql server create -n "$SQL_SERVER" -g "$RESOURCE_GROUP" -l "$SQL_LOCATION" \
    --admin-user "$SQL_ADMIN" --admin-password "$SQL_ADMIN_PASSWORD" --minimal-tls-version 1.2 \
    --only-show-errors -o none
elif [ "$PASSWORD_SOURCE" != "existing" ]; then
  log "Setting SQL admin password on existing server ($PASSWORD_SOURCE)"
  az sql server update -n "$SQL_SERVER" -g "$RESOURCE_GROUP" --admin-password "$SQL_ADMIN_PASSWORD" \
    --only-show-errors -o none
fi
SQL_FQDN="$(az sql server show -n "$SQL_SERVER" -g "$RESOURCE_GROUP" --query fullyQualifiedDomainName -o tsv)"

az sql server firewall-rule create -g "$RESOURCE_GROUP" -s "$SQL_SERVER" -n AllowAllWindowsAzureIps \
  --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 --only-show-errors -o none

if ! az sql db show -n "$SQL_DB" -s "$SQL_SERVER" -g "$RESOURCE_GROUP" -o none 2>/dev/null; then
  log "Creating database $SQL_DB (Basic, 5 DTU)"
  az sql db create -n "$SQL_DB" -s "$SQL_SERVER" -g "$RESOURCE_GROUP" --edition Basic \
    --capacity 5 --backup-storage-redundancy Local --only-show-errors -o none
fi

HOST_IP="$(curl -fsS https://api.ipify.org)"
log "Schema load (temporary firewall rule for this host)"
az sql server firewall-rule create -g "$RESOURCE_GROUP" -s "$SQL_SERVER" -n "$HOST_IP_RULE" \
  --start-ip-address "$HOST_IP" --end-ip-address "$HOST_IP" --only-show-errors -o none
win_path() { if command -v cygpath >/dev/null 2>&1; then cygpath -w -a -l "$1"; else printf '%s' "$1"; fi; }  # -l: long names, Docker Desktop cannot mount 8.3 short paths
SQLCMDPASSWORD="$SQL_ADMIN_PASSWORD" docker run --rm -e SQLCMDPASSWORD \
  -v "$(win_path "$SCRIPT_DIR")":/deploy:ro \
  -v "$(win_path "$SOURCE_DIR/AttendanceSystem/Database")":/legacy:ro \
  -v "$(win_path "$SOURCE_DIR/db/init")":/db-init:ro \
  --entrypoint /bin/bash "$MSSQL_IMAGE" /deploy/init-azure-sql.sh "$SQL_FQDN" "$SQL_DB" "$SQL_ADMIN"
az sql server firewall-rule delete -g "$RESOURCE_GROUP" -s "$SQL_SERVER" -n "$HOST_IP_RULE" \
  --only-show-errors -o none

CONN="Server=tcp:$SQL_FQDN,1433;Database=$SQL_DB;User Id=$SQL_ADMIN;Password=$SQL_ADMIN_PASSWORD;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"

# ---- Container Apps ----------------------------------------------------------------------------
if ! az containerapp env show -n "$CAE_NAME" -g "$RESOURCE_GROUP" -o none 2>/dev/null; then
  log "Creating Container Apps environment $CAE_NAME"
  az containerapp env create -n "$CAE_NAME" -g "$RESOURCE_GROUP" -l "$LOCATION" --only-show-errors -o none
fi

if ! az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" -o none 2>/dev/null; then
  log "Creating container app $APP_NAME (placeholder image until AcrPull is granted)"
  az containerapp create -n "$APP_NAME" -g "$RESOURCE_GROUP" --environment "$CAE_NAME" \
    --image mcr.microsoft.com/k8se/quickstart:latest --ingress external --target-port 8080 \
    --system-assigned --min-replicas "$MIN_REPLICAS" --max-replicas "$MAX_REPLICAS" \
    --cpu 0.5 --memory 1.0Gi --only-show-errors -o none
fi
az containerapp identity assign -n "$APP_NAME" -g "$RESOURCE_GROUP" --system-assigned --only-show-errors -o none
PRINCIPAL_ID="$(az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" --query identity.principalId -o tsv)"
if [ -z "$(az role assignment list --assignee "$PRINCIPAL_ID" --scope "$ACR_ID" --role AcrPull --query '[0].id' -o tsv)" ]; then
  log "Granting AcrPull to the app's managed identity"
  az role assignment create --assignee-object-id "$PRINCIPAL_ID" --assignee-principal-type ServicePrincipal \
    --role AcrPull --scope "$ACR_ID" --only-show-errors -o none
fi
az containerapp registry set -n "$APP_NAME" -g "$RESOURCE_GROUP" --server "$ACR_SERVER" --identity system \
  --only-show-errors -o none

az containerapp secret set -n "$APP_NAME" -g "$RESOURCE_GROUP" --secrets "sqlconn=$CONN" --only-show-errors -o none

log "Updating $APP_NAME to $IMAGE"
for attempt in 1 2 3 4 5 6; do   # role assignment propagation can take a minute
  if az containerapp update -n "$APP_NAME" -g "$RESOURCE_GROUP" --image "$IMAGE" \
       --min-replicas "$MIN_REPLICAS" --max-replicas "$MAX_REPLICAS" \
       --set-env-vars "ASPNETCORE_ENVIRONMENT=Development" "ConnectionStrings__Default=secretref:sqlconn" \
       --revision-suffix "r$(date +%s)" --only-show-errors -o none; then
    break
  fi
  [ "$attempt" -eq 6 ] && { echo "container app update failed" >&2; exit 1; }
  sleep 20
done

FQDN="$(az containerapp show -n "$APP_NAME" -g "$RESOURCE_GROUP" --query properties.configuration.ingress.fqdn -o tsv)"
BASE="https://$FQDN"

# ---- verification ------------------------------------------------------------------------------
log "Waiting for $BASE/health"
status=""
for _ in $(seq 1 36); do
  status="$(curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$BASE/health" || true)"
  [ "$status" = "200" ] && break
  sleep 5
done
if [ "$status" != "200" ]; then
  echo "health check failed (last status $status); recent logs:" >&2
  az containerapp logs show -n "$APP_NAME" -g "$RESOURCE_GROUP" --tail 50 --type console >&2 || true
  exit 1
fi

check() {  # check <expected-status> <method> <path> [json-body]
  local expected="$1" method="$2" path="$3" body="${4:-}" out code
  out="$WORK_DIR/resp.json"
  if [ -n "$body" ]; then
    code="$(curl -s -o "$out" -w '%{http_code}' -X "$method" -H 'Content-Type: application/json' -d "$body" "$BASE$path")"
  else
    code="$(curl -s -o "$out" -w '%{http_code}' -X "$method" "$BASE$path")"
  fi
  printf '%s %s -> %s\n' "$method" "$path" "$code"
  head -c 1500 "$out"; echo
  [ "$code" = "$expected" ]
}

TODAY="$(date -u +%Y-%m-%d)"
SMOKE_BODY="{\"schoolId\":1,\"attendDate\":\"$TODAY\",\"entries\":[{\"studentId\":1,\"attendCode\":\"P\"},{\"studentId\":2,\"attendCode\":\"A\",\"notes\":\"smoke test\"},{\"studentId\":3,\"attendCode\":\"L\",\"minutesLate\":10}]}"
smoke_ok=true
check 200 GET /health || smoke_ok=false
code="$(curl -s -o /dev/null -w '%{http_code}' "$BASE/swagger/index.html")"; echo "GET /swagger/index.html -> $code"
[ "$code" = "200" ] || smoke_ok=false
check 200 POST /api/attendance/daily "$SMOKE_BODY" || smoke_ok=false
check 200 GET /api/students/2/attendance || smoke_ok=false
check 200 GET /api/students/2/absenteeism || smoke_ok=false

if [ "$smoke_ok" != "true" ]; then
  echo "smoke tests failed; ingress left unrestricted for diagnosis" >&2
  exit 1
fi

if [ "$RESTRICT_INGRESS" = "true" ]; then
  log "Restricting ingress to $HOST_IP (API has no authentication, review finding H1)"
  az containerapp ingress access-restriction set -n "$APP_NAME" -g "$RESOURCE_GROUP" \
    --rule-name allow-deploy-host --ip-address "$HOST_IP/32" --action Allow --only-show-errors -o none
fi

log "Deployed $COMMIT_SHA to $BASE"
echo "Swagger: $BASE/swagger"
