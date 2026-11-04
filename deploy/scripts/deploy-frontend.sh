#!/usr/bin/env bash
# ==============================================================================
# DrinksEcommerce — Frontend Controlled Deployment & Rollback Script (EC2)
# Executed remotely by GitHub Actions via AWS Systems Manager (SSM SendCommand)
# ==============================================================================
set -euo pipefail

NEW_IMAGE="${1:?Usage: deploy-frontend.sh <dockerhub-repo>:<commit-sha>}"
APP_DIR="/opt/drinks-ecommerce"
cd "${APP_DIR}"

echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] Starting frontend deployment for image: ${NEW_IMAGE}"

PREV_FRONTEND_IMAGE=""
if [ -f "${APP_DIR}/images.env" ]; then
  source "${APP_DIR}/images.env"
  PREV_FRONTEND_IMAGE="${FRONTEND_IMAGE:-}"
fi
BACKEND_IMAGE="${BACKEND_IMAGE:-akintoyesodeinde/drinks-ecommerce-backend:latest}"

# 1. Pull the immutable frontend image tag from Docker Hub
docker pull "${NEW_IMAGE}"

# 2. Update image state and re-sync .env (never touches database or backend state)
cat > "${APP_DIR}/images.env" <<EOF
BACKEND_IMAGE=${BACKEND_IMAGE}
FRONTEND_IMAGE=${NEW_IMAGE}
EOF
chmod 600 "${APP_DIR}/images.env"
"${APP_DIR}/scripts/sync-secrets.sh"

# 3. Roll only the frontend container
docker compose --env-file "${APP_DIR}/.env" up -d --no-deps frontend

# 4. Verify frontend container health (/healthz and HTTP 200 on /)
HEALTHY="false"
for i in {1..20}; do
  if curl -fsS "http://127.0.0.1:80/healthz" >/dev/null 2>&1 && curl -fsS "http://127.0.0.1:80/" >/dev/null 2>&1; then
    HEALTHY="true"
    break
  fi
  sleep 2
done

if [ "${HEALTHY}" != "true" ]; then
  echo "ERROR: Frontend container failed health checks after deploying ${NEW_IMAGE}."
  docker logs --tail 60 drinks-frontend || true
  if [ -n "${PREV_FRONTEND_IMAGE}" ] && [ "${PREV_FRONTEND_IMAGE}" != "${NEW_IMAGE}" ]; then
    echo "Rolling back frontend to previous image: ${PREV_FRONTEND_IMAGE}"
    cat > "${APP_DIR}/images.env" <<EOF
BACKEND_IMAGE=${BACKEND_IMAGE}
FRONTEND_IMAGE=${PREV_FRONTEND_IMAGE}
EOF
    "${APP_DIR}/scripts/sync-secrets.sh"
    docker compose --env-file "${APP_DIR}/.env" up -d --no-deps frontend
    echo "$(date -u +'%Y-%m-%dT%H:%M:%SZ') | FRONTEND_ROLLBACK | failed=${NEW_IMAGE} | restored=${PREV_FRONTEND_IMAGE}" >> "${APP_DIR}/releases.log"
  fi
  exit 1
fi

echo "$(date -u +'%Y-%m-%dT%H:%M:%SZ') | FRONTEND_DEPLOY_SUCCESS | image=${NEW_IMAGE} | previous=${PREV_FRONTEND_IMAGE:-none}" >> "${APP_DIR}/releases.log"
docker image prune -f >/dev/null 2>&1 || true
echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] Frontend deployment verified healthy: ${NEW_IMAGE}"
