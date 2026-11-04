#!/usr/bin/env bash
# ==============================================================================
# DrinksEcommerce — Let's Encrypt HTTPS Enablement Script (EC2)
# Run only after the approved domain/subdomain A-record resolves to this EC2 IP.
# ==============================================================================
set -euo pipefail

DOMAIN="${1:?Usage: enable-https.sh <domain-name> <admin-email>}"
EMAIL="${2:?Usage: enable-https.sh <domain-name> <admin-email>}"
APP_DIR="/opt/drinks-ecommerce"

echo "Requesting Let's Encrypt TLS certificate for ${DOMAIN}..."
docker run --rm \
  -v "${APP_DIR}/certs:/etc/letsencrypt" \
  -v "${APP_DIR}/certbot-www:/var/www/certbot" \
  -p 80:80 \
  certbot/certbot certonly --standalone \
  --non-interactive --agree-tos \
  --email "${EMAIL}" \
  -d "${DOMAIN}"

echo "Certificate issued in ${APP_DIR}/certs/live/${DOMAIN}/."
