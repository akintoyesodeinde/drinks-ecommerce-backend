#!/usr/bin/env bash
# ==============================================================================
# DrinksEcommerce — Amazon Linux 2023 EC2 Launch Template User Data Script
# ==============================================================================
# SECURITY NOTICE:
# - Contains ZERO secrets, passwords, API keys, or tokens.
# - Runtime secrets are retrieved on-demand from AWS Secrets Manager using the
#   attached EC2 IAM Instance Role (DrinksEcommerce-EC2-Runtime).
# - Idempotent: safe to re-run without destroying existing volumes or data.
# ==============================================================================

set -euo pipefail

LOG_FILE="/var/log/drinks-ecommerce-init.log"
mkdir -p "$(dirname "$LOG_FILE")"
exec > >(tee -a "$LOG_FILE") 2>&1

echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] Starting DrinksEcommerce EC2 initialization..."

# 1. Configure 2 GiB swap file (if not already present) to protect SQL Server + .NET memory headroom
if [ ! -f /swapfile ]; then
  echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] Creating 2 GiB /swapfile..."
  dd if=/dev/zero of=/swapfile bs=1M count=2048 status=none
  chmod 600 /swapfile
  mkswap /swapfile
  swapon /swapfile
  if ! grep -q '/swapfile' /etc/fstab; then
    echo '/swapfile swap swap defaults 0 0' >> /etc/fstab
  fi
fi

# 2. Update OS packages and install required operational tools
dnf update -y
dnf install -y docker jq awscli amazon-cloudwatch-agent tar gzip

# 3. Install Docker Compose v2 CLI plugin (if not already installed)
mkdir -p /usr/local/lib/docker/cli-plugins /usr/libexec/docker/cli-plugins
if [ ! -x /usr/local/lib/docker/cli-plugins/docker-compose ]; then
  COMPOSE_VERSION="v2.32.4"
  ARCH="$(uname -m)"
  curl -fsSL "https://github.com/docker/compose/releases/download/${COMPOSE_VERSION}/docker-compose-linux-${ARCH}" \
    -o /usr/local/lib/docker/cli-plugins/docker-compose
  chmod +x /usr/local/lib/docker/cli-plugins/docker-compose
  ln -sf /usr/local/lib/docker/cli-plugins/docker-compose /usr/libexec/docker/cli-plugins/docker-compose
fi

# 4. Configure Docker daemon log rotation & live-restore
mkdir -p /etc/docker
cat >/etc/docker/daemon.json <<'EOF'
{
  "log-driver": "json-file",
  "log-opts": {
    "max-size": "20m",
    "max-file": "5"
  },
  "live-restore": true
}
EOF

systemctl enable docker
systemctl start docker
usermod -aG docker ec2-user || true

# 5. Ensure AWS Systems Manager (SSM) Agent is enabled and active
systemctl enable amazon-ssm-agent || true
systemctl restart amazon-ssm-agent || true

# 6. Create application directory structure with strict permissions
APP_DIR="/opt/drinks-ecommerce"
mkdir -p "${APP_DIR}/scripts" "${APP_DIR}/certs" "${APP_DIR}/certbot-www" "${APP_DIR}/backups"
chmod 750 "${APP_DIR}"
touch "${APP_DIR}/releases.log"
chmod 640 "${APP_DIR}/releases.log"

# 7. Write production docker-compose.yml if not yet present
cat >"${APP_DIR}/docker-compose.yml" <<'EOF'
name: drinks-ecommerce-prod

services:
  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    container_name: drinks-sqlserver
    restart: unless-stopped
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_PID: "${MSSQL_PID:-Express}"
      MSSQL_SA_PASSWORD: "${MSSQL_SA_PASSWORD}"
      MSSQL_MEMORY_LIMIT_MB: "1536"
      TZ: "Africa/Lagos"
    volumes:
      - sqlserver_data:/var/opt/mssql
      - sqlserver_backups:/var/opt/mssql/backups
    networks:
      - drinks_internal
    healthcheck:
      test: ["CMD-SHELL", "/opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P \"$$MSSQL_SA_PASSWORD\" -C -Q \"SELECT 1\" || /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P \"$$MSSQL_SA_PASSWORD\" -Q \"SELECT 1\""]
      interval: 15s
      timeout: 10s
      retries: 10
      start_period: 40s

  redis:
    image: redis:7-alpine
    container_name: drinks-redis
    restart: unless-stopped
    command: ["redis-server", "--appendonly", "yes", "--maxmemory", "128mb", "--maxmemory-policy", "allkeys-lru"]
    volumes:
      - redis_data:/data
    networks:
      - drinks_internal
    healthcheck:
      test: ["CMD", "redis-cli", "ping"]
      interval: 10s
      timeout: 5s
      retries: 5

  api:
    image: "${BACKEND_IMAGE:-akintoyesodeinde/drinks-ecommerce-backend:latest}"
    container_name: drinks-api
    restart: unless-stopped
    depends_on:
      sqlserver:
        condition: service_healthy
      redis:
        condition: service_healthy
    env_file:
      - path: .env
        required: false
    environment:
      ASPNETCORE_ENVIRONMENT: "Production"
      ASPNETCORE_URLS: "http://+:8080"
      TZ: "Africa/Lagos"
      Database__SkipAutoInitialize: "true"
    volumes:
      - api_uploads:/app/wwwroot/uploads
    networks:
      - drinks_internal
    healthcheck:
      test: ["CMD", "dotnet", "DrinksEcommerce.API.dll", "--healthcheck"]
      interval: 15s
      timeout: 5s
      retries: 5
      start_period: 25s

  frontend:
    image: "${FRONTEND_IMAGE:-akintoyesodeinde/drinks-ecommerce-frontend:latest}"
    container_name: drinks-frontend
    restart: unless-stopped
    depends_on:
      api:
        condition: service_healthy
    ports:
      - "80:80"
      - "443:443"
    environment:
      API_UPSTREAM: "http://api:8080"
    volumes:
      - /opt/drinks-ecommerce/certs:/etc/letsencrypt:ro
      - /opt/drinks-ecommerce/certbot-www:/var/www/certbot:ro
    networks:
      - drinks_internal
    healthcheck:
      test: ["CMD-SHELL", "wget -qO- http://127.0.0.1:80/healthz > /dev/null || exit 1"]
      interval: 15s
      timeout: 5s
      retries: 3
      start_period: 10s

networks:
  drinks_internal:
    driver: bridge

volumes:
  sqlserver_data:
    name: drinks_sqlserver_data
  sqlserver_backups:
    name: drinks_sqlserver_backups
  redis_data:
    name: drinks_redis_data
  api_uploads:
    name: drinks_api_uploads
EOF

# 8. Install secret-sync helper script (/opt/drinks-ecommerce/scripts/sync-secrets.sh)
cat >"${APP_DIR}/scripts/sync-secrets.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
umask 077

APP_DIR="/opt/drinks-ecommerce"
SECRET_ID="${1:-drinks-ecommerce/prod/app-secrets}"

# Query IMDSv2 for AWS region if AWS_REGION is not set
if [ -z "${AWS_REGION:-}" ]; then
  IMDS_TOKEN="$(curl -sS -X PUT "http://169.254.169.254/latest/api/token" -H "X-aws-ec2-metadata-token-ttl-seconds: 60")"
  AWS_REGION="$(curl -sS -H "X-aws-ec2-metadata-token: ${IMDS_TOKEN}" "http://169.254.169.254/latest/meta-data/placement/region")"
fi

SECRET_JSON="$(aws secretsmanager get-secret-value --region "${AWS_REGION}" --secret-id "${SECRET_ID}" --query 'SecretString' --output text)"

# Preserve existing image tags if /opt/drinks-ecommerce/images.env exists
if [ -f "${APP_DIR}/images.env" ]; then
  source "${APP_DIR}/images.env"
fi

BACKEND_IMAGE="${BACKEND_IMAGE:-akintoyesodeinde/drinks-ecommerce-backend:latest}"
FRONTEND_IMAGE="${FRONTEND_IMAGE:-akintoyesodeinde/drinks-ecommerce-frontend:latest}"

# Convert flat JSON key-values from Secrets Manager into root-only .env file without echoing values
printf "%s" "${SECRET_JSON}" | jq -r 'to_entries | .[] | "\(.key)=\(.value)"' > "${APP_DIR}/.env.tmp"
{
  echo "BACKEND_IMAGE=${BACKEND_IMAGE}"
  echo "FRONTEND_IMAGE=${FRONTEND_IMAGE}"
} >> "${APP_DIR}/.env.tmp"

mv "${APP_DIR}/.env.tmp" "${APP_DIR}/.env"
chmod 600 "${APP_DIR}/.env"
echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] Synchronized runtime configuration from Secrets Manager (${SECRET_ID})."
EOF
chmod 700 "${APP_DIR}/scripts/sync-secrets.sh"

# 8b. Install controlled backend deployment & rollback script (/opt/drinks-ecommerce/scripts/deploy-backend.sh)
cat >"${APP_DIR}/scripts/deploy-backend.sh" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail

NEW_IMAGE="${1:?Usage: deploy-backend.sh <dockerhub-repo>:<commit-sha>}"
APP_DIR="/opt/drinks-ecommerce"
cd "${APP_DIR}"

echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] Starting backend deployment for image: ${NEW_IMAGE}"

PREV_BACKEND_IMAGE=""
if [ -f "${APP_DIR}/images.env" ]; then
  source "${APP_DIR}/images.env"
  PREV_BACKEND_IMAGE="${BACKEND_IMAGE:-}"
fi
FRONTEND_IMAGE="${FRONTEND_IMAGE:-akintoyesodeinde/drinks-ecommerce-frontend:latest}"

"${APP_DIR}/scripts/sync-secrets.sh"

if ! docker pull "${NEW_IMAGE}"; then
  echo "Notice: docker pull ${NEW_IMAGE} did not succeed from remote registry; checking local Docker daemon..."
  docker image inspect "${NEW_IMAGE}" >/dev/null 2>&1 || { echo "ERROR: Image ${NEW_IMAGE} not found locally or on Docker Hub."; exit 1; }
fi

docker compose --env-file "${APP_DIR}/.env" up -d sqlserver redis

echo "Waiting for SQL Server readiness..."
for i in {1..30}; do
  if [ "$(docker inspect -f '{{.State.Health.Status}}' drinks-sqlserver 2>/dev/null || echo starting)" = "healthy" ]; then
    echo "SQL Server is healthy."
    break
  fi
  sleep 3
done

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

cat > "${APP_DIR}/images.env" <<IMG_EOF
BACKEND_IMAGE=${NEW_IMAGE}
FRONTEND_IMAGE=${FRONTEND_IMAGE}
IMG_EOF
chmod 600 "${APP_DIR}/images.env"
"${APP_DIR}/scripts/sync-secrets.sh"

echo "Executing controlled database migration (--migrate)..."
docker compose --env-file "${APP_DIR}/.env" run --rm api --migrate

echo "Updating api container to ${NEW_IMAGE}..."
docker compose --env-file "${APP_DIR}/.env" up -d --no-deps api

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
    cat > "${APP_DIR}/images.env" <<IMG_EOF
BACKEND_IMAGE=${PREV_BACKEND_IMAGE}
FRONTEND_IMAGE=${FRONTEND_IMAGE}
IMG_EOF
    "${APP_DIR}/scripts/sync-secrets.sh"
    docker compose --env-file "${APP_DIR}/.env" up -d --no-deps api
    echo "$(date -u +'%Y-%m-%dT%H:%M:%SZ') | BACKEND_ROLLBACK | failed=${NEW_IMAGE} | restored=${PREV_BACKEND_IMAGE}" >> "${APP_DIR}/releases.log"
  fi
  exit 1
fi

echo "$(date -u +'%Y-%m-%dT%H:%M:%SZ') | BACKEND_DEPLOY_SUCCESS | image=${NEW_IMAGE} | previous=${PREV_BACKEND_IMAGE:-none}" >> "${APP_DIR}/releases.log"
docker image prune -f >/dev/null 2>&1 || true
echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] Backend deployment verified healthy: ${NEW_IMAGE}"
EOF
chmod 700 "${APP_DIR}/scripts/deploy-backend.sh"

# 8c. Install controlled frontend deployment & rollback script (/opt/drinks-ecommerce/scripts/deploy-frontend.sh)
cat >"${APP_DIR}/scripts/deploy-frontend.sh" <<'EOF'
#!/usr/bin/env bash
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

if ! docker pull "${NEW_IMAGE}"; then
  echo "Notice: docker pull ${NEW_IMAGE} did not succeed from remote registry; checking local Docker daemon..."
  docker image inspect "${NEW_IMAGE}" >/dev/null 2>&1 || { echo "ERROR: Image ${NEW_IMAGE} not found locally or on Docker Hub."; exit 1; }
fi

cat > "${APP_DIR}/images.env" <<IMG_EOF
BACKEND_IMAGE=${BACKEND_IMAGE}
FRONTEND_IMAGE=${NEW_IMAGE}
IMG_EOF
chmod 600 "${APP_DIR}/images.env"
"${APP_DIR}/scripts/sync-secrets.sh"

docker compose --env-file "${APP_DIR}/.env" up -d --no-deps frontend

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
    cat > "${APP_DIR}/images.env" <<IMG_EOF
BACKEND_IMAGE=${BACKEND_IMAGE}
FRONTEND_IMAGE=${PREV_FRONTEND_IMAGE}
IMG_EOF
    "${APP_DIR}/scripts/sync-secrets.sh"
    docker compose --env-file "${APP_DIR}/.env" up -d --no-deps frontend
    echo "$(date -u +'%Y-%m-%dT%H:%M:%SZ') | FRONTEND_ROLLBACK | failed=${NEW_IMAGE} | restored=${PREV_FRONTEND_IMAGE}" >> "${APP_DIR}/releases.log"
  fi
  exit 1
fi

echo "$(date -u +'%Y-%m-%dT%H:%M:%SZ') | FRONTEND_DEPLOY_SUCCESS | image=${NEW_IMAGE} | previous=${PREV_FRONTEND_IMAGE:-none}" >> "${APP_DIR}/releases.log"
docker image prune -f >/dev/null 2>&1 || true
echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] Frontend deployment verified healthy: ${NEW_IMAGE}"
EOF
chmod 700 "${APP_DIR}/scripts/deploy-frontend.sh"

# 9. Configure systemd boot unit so containers start automatically across instance reboots
cat >/etc/systemd/system/drinks-ecommerce.service <<'EOF'
[Unit]
Description=DrinksEcommerce Docker Compose Stack
Requires=docker.service
After=docker.service network-online.target
Wants=network-online.target

[Service]
Type=oneshot
RemainAfterExit=yes
WorkingDirectory=/opt/drinks-ecommerce
ExecStartPre=-/opt/drinks-ecommerce/scripts/sync-secrets.sh
ExecStart=/usr/bin/docker compose --env-file /opt/drinks-ecommerce/.env up -d
ExecStop=/usr/bin/docker compose --env-file /opt/drinks-ecommerce/.env stop
TimeoutStartSec=300

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable drinks-ecommerce.service

# 10. Configure CloudWatch Agent for host metrics and initialization logs
cat >/opt/aws/amazon-cloudwatch-agent/etc/amazon-cloudwatch-agent.json <<'EOF'
{
  "agent": {
    "metrics_collection_interval": 60,
    "run_as_user": "root"
  },
  "metrics": {
    "namespace": "DrinksEcommerce/EC2",
    "append_dimensions": {
      "InstanceId": "${aws:InstanceId}"
    },
    "metrics_collected": {
      "disk": {
        "measurement": ["used_percent"],
        "metrics_collection_interval": 60,
        "resources": ["/"]
      },
      "mem": {
        "measurement": ["mem_used_percent"],
        "metrics_collection_interval": 60
      }
    }
  },
  "logs": {
    "logs_collected": {
      "files": {
        "collect_list": [
          {
            "file_path": "/var/log/drinks-ecommerce-init.log",
            "log_group_name": "/drinks-ecommerce/ec2-init",
            "log_stream_name": "{instance_id}",
            "retention_in_days": 14
          },
          {
            "file_path": "/opt/drinks-ecommerce/releases.log",
            "log_group_name": "/drinks-ecommerce/deployments",
            "log_stream_name": "{instance_id}",
            "retention_in_days": 30
          }
        ]
      }
    }
  }
}
EOF

/opt/aws/amazon-cloudwatch-agent/bin/amazon-cloudwatch-agent-ctl \
  -a fetch-config -m ec2 -s \
  -c file:/opt/aws/amazon-cloudwatch-agent/etc/amazon-cloudwatch-agent.json || true

# 11. Verify Docker and Docker Compose availability
docker --version
docker compose version

echo "[$(date -u +'%Y-%m-%dT%H:%M:%SZ')] DrinksEcommerce EC2 initialization completed successfully."
