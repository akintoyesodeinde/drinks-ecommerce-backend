# DrinksEcommerce — Deployment Guide

## 1. Container Images

The platform builds three production-oriented Docker images plus stateful infrastructure services in `docker-compose.yml`:

| Service | Dockerfile | Target / Base Image | Internal Port | Default Host Port |
|---|---|---|---|---|
| `api` | `backend/Dockerfile` | `mcr.microsoft.com/dotnet/aspnet:10.0` (non-root `app` user) | `8080` | `5080` |
| `web` | `frontend/drinks-ecommerce-web/Dockerfile` | `nginx:1.27-alpine` serving Vite build + `/api` reverse proxy | `80` | `3000` |
| `mobile-web` | `mobile/drinks-ecommerce-mobile/Dockerfile` (`target: web`) | `nginx:1.27-alpine` serving Expo web bundle + `/api` reverse proxy | `80` | `8082` |
| `mobile-dev` | `mobile/drinks-ecommerce-mobile/Dockerfile` (`target: dev`) | `node:22-alpine` running Expo dev server (optional profile `mobile-dev`) | `8081` | `8081` |
| `sqlserver` | `mcr.microsoft.com/mssql/server:2022-latest` | SQL Server 2022 Developer | `1433` | `1433` |
| `redis` | `redis:7-alpine` | Redis 7 distributed catalog cache | `6379` | `6379` |

## 2. Building Images Individually

```powershell
# 1. ASP.NET Core 10 Backend API
docker build -f backend/Dockerfile -t drinksecommerce-api:latest backend

# 2. React + TypeScript + Vite Web Storefront
docker build -f frontend/drinks-ecommerce-web/Dockerfile -t drinksecommerce-web:latest frontend/drinks-ecommerce-web

# 3. React Native + TypeScript + Expo Mobile App (Web Production Bundle)
docker build -f mobile/drinks-ecommerce-mobile/Dockerfile --target web -t drinksecommerce-mobile-web:latest mobile/drinks-ecommerce-mobile

# 4. React Native + TypeScript + Expo Mobile App (Dev Server Image)
docker build -f mobile/drinks-ecommerce-mobile/Dockerfile --target dev -t drinksecommerce-mobile-dev:latest mobile/drinks-ecommerce-mobile
```

## 3. Production Readiness Checklist Before Live Launch

- [ ] Replace `Jwt__SigningKey` with a cryptographically random secret (≥ 64 bytes) stored in a managed secret vault.
- [ ] Configure `Payments__Provider=Paystack` and supply live Paystack keys (`sk_live_...`, `pk_live_...`) only after completing Paystack business verification.
- [ ] Configure HTTPS TLS termination with HSTS and restrict `Cors__AllowedOrigins` to the production domain.
- [ ] Set `Database__SeedInContainer=false` in production and apply EF Core migrations via controlled deployment pipeline (`dotnet ef database update`).
- [ ] Configure an external transactional SMTP provider (`Email__Provider=Smtp`).
- [ ] Keep `Store__AlcoholSalesEnabled=false` until merchant liquor licensing, age-verification, and delivery policies receive legal approval.
