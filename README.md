# DrinksEcommerce Nigeria — ASP.NET Core 10 Backend API (`drinks-ecommerce-backend`)

Production-oriented **ASP.NET Core 10 LTS (`net10.0`) / C# 14** modular monolith backend for **DrinksEcommerce**, a Nigerian single-store online drinks retailer with **Entity Framework Core 10**, **SQL Server**, **ASP.NET Core Identity**, and **Paystack** payment gateway integration.

- **Backend Repository**: `https://github.com/akintoyesodeinde/drinks-ecommerce-backend`
- **Frontend Repository**: `https://github.com/akintoyesodeinde/drinks-ecommerce-frontend`

---

## 1. Clean Architecture Structure

```text
backend/
├── DrinksEcommerce.sln
├── global.json
├── Directory.Build.props
├── Directory.Packages.props
├── Dockerfile
├── .env.example
├── src/
│   ├── DrinksEcommerce.Domain/          # Entities, Nigerian value rules (NGN/kobo, +234 phones), enums
│   ├── DrinksEcommerce.Application/     # Catalog, Cart/Checkout, Paystack Orchestrator, Order/Admin services
│   ├── DrinksEcommerce.Persistence/     # EF Core 10 ApplicationDbContext, SQL Server Migrations, Seeder
│   ├── DrinksEcommerce.Infrastructure/  # Paystack HTTP gateway, Redis/Memory cache, SMTP, background worker
│   └── DrinksEcommerce.API/             # Minimal APIs, JWT + rotating refresh tokens, health checks
├── tests/
│   ├── DrinksEcommerce.UnitTests/
│   ├── DrinksEcommerce.ArchitectureTests/
│   └── DrinksEcommerce.IntegrationTests/
├── deploy/
│   ├── ec2-user-data.sh                 # Idempotent Amazon Linux 2023 EC2 Launch Template User Data
│   ├── docker-compose.prod.yml          # Production Compose stack (SQL Server + Redis + API + Nginx)
│   ├── scripts/                         # Controlled migration, deploy, rollback, and Secrets Manager sync
│   └── aws/                             # IAM OIDC trust policy, least-privilege policies, provision script
└── docs/                                # Architecture, API Reference, Database, Paystack, Security & Runbook
```

---

## 2. Build, Test & Database Migrations

```bash
dotnet restore DrinksEcommerce.sln
dotnet build DrinksEcommerce.sln --configuration Release --no-restore
dotnet test DrinksEcommerce.sln --configuration Release --no-build
```

Run controlled database migrations and production catalog seeding via the built-in CLI flag:

```bash
dotnet run --project src/DrinksEcommerce.API -- --migrate
```

---

## 3. Automated CI/CD (GitHub Actions + Docker Hub + AWS OIDC)

Defined in [`.github/workflows/deploy.yml`](.github/workflows/deploy.yml):
1. Restores, builds, and executes all unit (`23`), Clean Architecture (`3`), and SQL Server end-to-end integration tests (`15-step purchase-to-refund lifecycle`).
2. Builds and pushes `akintoyesodeinde/drinks-ecommerce-backend:<commit-sha>` and `:latest` to Docker Hub using `DOCKERHUB_USERNAME` and `DOCKERHUB_TOKEN`.
3. Authenticates to an AWS Account
4. Dispatches an AWS Systems Manager (`SSM SendCommand`) execution of `deploy/scripts/deploy-backend.sh` on the `Project=DrinksEcommerce` EC2 instance:
   - Synchronizes runtime secrets from AWS Secrets Manager.
   - Takes a pre-migration SQL Server backup.
   - Runs `docker compose run --rm api --migrate`.
   - Performs a rolling update of `drinks-api` to `:<commit-sha>`.
   - Verifies `/health/live` and `/health/ready`, and automatically rolls back to the previous image SHA if health checks fail.
