#!/usr/bin/env bash
# ==============================================================================
# DrinksEcommerce — Backend Controlled Deployment & Rollback Script (EC2)
# Executed remotely by GitHub Actions via AWS Systems Manager (SSM SendCommand)
# ==============================================================================
set -euo pipefail

NEW_IMAGE="${1:?Usage: deploy-backend.sh <dockerhub-repo>:<commit-sha>}"
APP_DIR="/opt/drinks-ecommerce"
cd "${APP_DIR}"

echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] Starting backend deployment for image: ${NEW_IMAGE}"

# 1. Record current deployed backend image for rollback
PREV_BACKEND_IMAGE=""
if [ -f "${APP_DIR}/images.env" ]; then
  source "${APP_DIR}/images.env"
  PREV_BACKEND_IMAGE="${BACKEND_IMAGE:-}"
fi
FRONTEND_IMAGE="${FRONTEND_IMAGE:-akintoyesodeinde/drinks-ecommerce-frontend:latest}"

# 2. Refresh secrets from AWS Secrets Manager
"${APP_DIR}/scripts/sync-secrets.sh"

# 3. Verify and pull the immutable backend image tag from Docker Hub
docker pull "${NEW_IMAGE}"

# 4. Ensure sqlserver and redis are running and healthy without touching existing volumes
docker compose --env-file "${APP_DIR}/.env" up -d sqlserver redis

echo "Waiting for SQL Server readiness..."
for i in {1..30}; do
  if [ "$(docker inspect -f '{{.State.Health.Status}}' drinks-sqlserver 2>/dev/null || echo starting)" = "healthy" ]; then
    echo "SQL Server is healthy."
    break
  fi
  sleep 3
done

# 5. Pre-migration SQL Server backup (if DrinksEcommerceDb already exists)
BACKUP_TS="$(date -u +'%Y%m%d_%H%M%S')"
docker exec drinks-sqlserver bash -c '
  SQLCMD="/opt/mssql-tools18/bin/sqlcmd -C"
  [ -x /opt/mssql-tools18/bin/sqlcmd ] || SQLCMD="/opt/mssql-tools/bin/sqlcmd"
  $SQLCMD -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "
    IF DB_ID(N'\''DrinksEcommerceDb'\'') IS NOT NULL
    BEGIN
      BACKUP DATABASE [DrinksEcommerceDb]
      TO DISK = N'\''/var/opt/mssql/backups/DrinksEcommerceDb_pre_migrate_'"${BACKUP_TS}"'.bak'\''
      WITH INIT, COMPRESSION, STATS = 10;
    END"
' || echo "Notice: Pre-migration backup skipped (initial database creation or Express compression fallback)."

# 6. Update image state and re-sync .env
cat > "${APP_DIR}/images.env" <<EOF
BACKEND_IMAGE=${NEW_IMAGE}
FRONTEND_IMAGE=${FRONTEND_IMAGE}
EOF
chmod 600 "${APP_DIR}/images.env"
"${APP_DIR}/scripts/sync-secrets.sh"

# 7. Run controlled EF Core migration & production catalog/bootstrap-admin seed step
echo "Executing controlled database migration (--migrate)..."
docker compose --env-file "${APP_DIR}/.env" run --rm api --migrate

# 8. Roll the API container to the new image
echo "Updating api container to ${NEW_IMAGE}..."
docker compose --env-file "${APP_DIR}/.env" up -d --no-deps api

# 9. Verify backend health checks (/health/live and /health/ready)
HEALTHY="false"
for i in {1..24}; do
  STATUS="$(docker inspect -f '{{.State.Health.Status}}' drinks-api 2>/dev/null || echo starting)"
  if [ "${STATUS}" = "healthy" ]; then
    if docker exec drinks-api dotnet DrinksEcommerce.API.dll --healthcheck; then
      HEALTHY="true"
      break
    fi
  fi
  sleep 3
done

if [ "${HEALTHY}" != "true" ]; then
  echo "ERROR: Backend container failed health checks after deploying ${NEW_IMAGE}."
  docker logs --tail 80 drinks-api || true
  if [ -n "${PREV_BACKEND_IMAGE}" ] && [ "${PREV_BACKEND_IMAGE}" != "${NEW_IMAGE}" ]; then
    echo "Rolling back backend to previous image: ${PREV_BACKEND_IMAGE}"
    cat > "${APP_DIR}/images.env" <<EOF
BACKEND_IMAGE=${PREV_BACKEND_IMAGE}
FRONTEND_IMAGE=${FRONTEND_IMAGE}
EOF
    "${APP_DIR}/scripts/sync-secrets.sh"
    docker compose --env-file "${APP_DIR}/.env" up -d --no-deps api
    echo "$(date -u +'%Y-%m-%dT%H:%M:%SZ') | BACKEND_ROLLBACK | failed=${NEW_IMAGE} | restored=${PREV_BACKEND_IMAGE}" >> "${APP_DIR}/releases.log"
  fi
  exit 1
fi

echo "$(date -u +'%Y-%m-%dT%H:%M:%SZ') | BACKEND_DEPLOY_SUCCESS | image=${NEW_IMAGE} | previous=${PREV_BACKEND_IMAGE:-none}" >> "${APP_DIR}/releases.log"
docker image prune -f >/dev/null 2>&1 || true
echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] Backend deployment verified healthy: ${NEW_IMAGE}"
