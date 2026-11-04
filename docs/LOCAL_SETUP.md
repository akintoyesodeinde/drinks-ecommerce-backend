# DrinksEcommerce — Local Setup Guide (Windows PowerShell)

## 1. Prerequisites

- **.NET 10 SDK** (`10.0.402` or later): `dotnet --list-sdks`
- **Node.js 22+** and **npm 10+**: `node --version`, `npm --version`
- **SQL Server**: SQL Server LocalDB (`(localdb)\mssqllocaldb`), SQL Server Developer Edition, or Docker Desktop (`mcr.microsoft.com/mssql/server:2022-latest`).

## 2. Backend Setup & Database Migrations

```powershell
# From the standalone backend folder
cd c:\Users\LENOVO\DrinksEcommerce\backend
dotnet restore DrinksEcommerce.sln
dotnet build DrinksEcommerce.sln --no-restore

# Apply EF Core migrations (automatically applied on API startup in Development)
dotnet ef database update --project src/DrinksEcommerce.Persistence --startup-project src/DrinksEcommerce.API

# Start the API on http://localhost:5080
dotnet run --project src/DrinksEcommerce.API/DrinksEcommerce.API.csproj
```

Verify the API is healthy:
```powershell
Invoke-RestMethod http://localhost:5080/health/live
Invoke-RestMethod http://localhost:5080/api/v1/products
```

## 3. Web Storefront & Admin Console (`frontend/drinks-ecommerce-web`)

```powershell
cd c:\Users\LENOVO\DrinksEcommerce\frontend\drinks-ecommerce-web
npm install
npm run dev
```
- Storefront URL: `http://localhost:5173`
- Admin Console URL: `http://localhost:5173/admin` (sign in with `admin@drinksecommerce.local` / `Admin#12345`)

### Production Build, Lint, and Tests
```powershell
npm run lint
npm test
npm run build
```

## 4. Mobile App (`mobile/drinks-ecommerce-mobile`)

```powershell
cd c:\Users\LENOVO\DrinksEcommerce\mobile\drinks-ecommerce-mobile
npm install
npm run typecheck
npm run lint
npm test
npx expo start
```

## 5. Docker Compose (Full Stack)

```powershell
cd c:\Users\LENOVO\DrinksEcommerce
Copy-Item .env.example .env -ErrorAction SilentlyContinue
docker compose up --build -d
docker compose ps
```

### Troubleshooting

- **SQL Server connection refused on local run**: Ensure SQL Server LocalDB is running (`sqllocaldb start mssqllocaldb`) or update `ConnectionStrings:DefaultConnection` in `appsettings.Development.json` to point to your SQL Server instance.
- **Docker Desktop 502 / pipe error on Windows**: Restart Docker Desktop (`Restart-Service com.docker.service` or restart Docker Desktop from the system tray) and wait until `docker info` reports the server version before running `docker compose up --build`.
