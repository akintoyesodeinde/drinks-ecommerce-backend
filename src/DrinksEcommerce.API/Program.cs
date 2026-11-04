using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using DrinksEcommerce.API.Endpoints;
using DrinksEcommerce.API.Middleware;
using DrinksEcommerce.API.Services;
using DrinksEcommerce.Application;
using DrinksEcommerce.Application.Common;
using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Infrastructure;
using DrinksEcommerce.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Serilog;

if (args.Contains("--healthcheck", StringComparer.OrdinalIgnoreCase))
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
    var healthUrl = Environment.GetEnvironmentVariable("HEALTHCHECK_URL") ?? "http://127.0.0.1:8080/health/live";
    try
    {
        using var resp = await http.GetAsync(healthUrl);
        Environment.Exit(resp.IsSuccessStatusCode ? 0 : 1);
    }
    catch
    {
        Environment.Exit(1);
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .Enrich.WithEnvironmentName()
    .WriteTo.Console());

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<AuthAndAccountService>();

builder.Services.AddApplication(builder.Configuration);
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);

// JWT Authentication
var jwtOpts = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOpts.SigningKey));

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = jwtOpts.Issuer,
        ValidateAudience = true,
        ValidAudience = jwtOpts.Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = signingKey,
        ValidateLifetime = true,
        ClockSkew = TimeSpan.FromSeconds(30)
    };
});

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("AdminOnly", p => p.RequireRole(Roles.Administrator))
    .AddPolicy("WarehouseOrAdmin", p => p.RequireRole(Roles.Administrator, Roles.WarehouseStaff))
    .AddPolicy("DeliveryOrAdmin", p => p.RequireRole(Roles.Administrator, Roles.DeliveryStaff));

// Rate Limiting
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("auth", limiter =>
    {
        limiter.PermitLimit = 30;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

// CORS restricted to configured frontend origins
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173", "http://localhost:8080", "http://localhost:8081", "http://localhost:19006"];

builder.Services.AddCors(options =>
{
    options.AddPolicy("FrontendPolicy", policy =>
    {
        policy.WithOrigins(allowedOrigins)
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders(CorrelationAndExceptionMiddleware.CorrelationHeader);
    });
});

// OpenAPI & Health Checks
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApplicationDbContext>("database", tags: ["ready"]);

var app = builder.Build();

app.UseMiddleware<CorrelationAndExceptionMiddleware>();
app.UseCors("FrontendPolicy");
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/v1.json");
}

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.MapHealthChecks("/api/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await System.Text.Json.JsonSerializer.SerializeAsync(context.Response.Body, new
        {
            status = report.Status.ToString(),
            totalDuration = report.TotalDuration,
            checks = report.Entries.Select(entry => new
            {
                name = entry.Key,
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
                duration = entry.Value.Duration
            })
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web), context.RequestAborted);
    }
});

app.MapCustomerEndpoints();
app.MapAdminEndpoints();
app.MapDevPaystackSimulatorEndpoints();

if (args.Contains("--migrate", StringComparer.OrdinalIgnoreCase))
{
    await DatabaseSeeder.InitializeAndSeedAsync(
        app.Services,
        app.Environment.IsDevelopment());
    return;
}

if (!string.Equals(app.Configuration["Database:SkipAutoInitialize"], "true", StringComparison.OrdinalIgnoreCase))
{
    await DatabaseSeeder.InitializeAndSeedAsync(
        app.Services,
        app.Environment.IsDevelopment());
}

app.Run();

public partial class Program;
