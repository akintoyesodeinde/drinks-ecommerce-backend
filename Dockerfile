# Stage 1: Build & Publish ASP.NET Core 10 Backend
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props DrinksEcommerce.sln ./
COPY src/DrinksEcommerce.Domain/DrinksEcommerce.Domain.csproj src/DrinksEcommerce.Domain/
COPY src/DrinksEcommerce.Application/DrinksEcommerce.Application.csproj src/DrinksEcommerce.Application/
COPY src/DrinksEcommerce.Persistence/DrinksEcommerce.Persistence.csproj src/DrinksEcommerce.Persistence/
COPY src/DrinksEcommerce.Infrastructure/DrinksEcommerce.Infrastructure.csproj src/DrinksEcommerce.Infrastructure/
COPY src/DrinksEcommerce.API/DrinksEcommerce.API.csproj src/DrinksEcommerce.API/

RUN dotnet restore src/DrinksEcommerce.API/DrinksEcommerce.API.csproj

COPY src/ src/
RUN dotnet publish src/DrinksEcommerce.API/DrinksEcommerce.API.csproj -c Release -o /app/publish /p:UseAppHost=false --no-restore

# Stage 2: Production-oriented ASP.NET Core 10 Runtime Image
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    TZ=Africa/Lagos

COPY --from=build /app/publish .
RUN mkdir -p /app/wwwroot/uploads/products && chown -R app:app /app

USER app
EXPOSE 8080

HEALTHCHECK --interval=15s --timeout=5s --start-period=25s --retries=5 \
  CMD ["dotnet", "DrinksEcommerce.API.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "DrinksEcommerce.API.dll"]
