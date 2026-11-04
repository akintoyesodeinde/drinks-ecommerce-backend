using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DrinksEcommerce.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            var cfg = sp.GetService<IConfiguration>() ?? configuration;
            var useInMemory = string.Equals(cfg["Database:Provider"], "InMemory", StringComparison.OrdinalIgnoreCase);

            if (useInMemory)
            {
                var dbName = cfg["Database:InMemoryName"] ?? "DrinksEcommerceInMemoryDb";
                options.UseInMemoryDatabase(dbName);
            }
            else
            {
                var rawConn = cfg.GetConnectionString("DefaultConnection");
                var connectionString = string.IsNullOrWhiteSpace(rawConn)
                    ? "Server=(localdb)\\mssqllocaldb;Database=DrinksEcommerceDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True"
                    : rawConn;

                options.UseSqlServer(connectionString, sql =>
                {
                    sql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                    sql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
                });
            }
        });

        services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<ApplicationDbContext>());

        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 8;
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        })
        .AddRoles<ApplicationRole>()
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddSignInManager()
        .AddDefaultTokenProviders();

        return services;
    }
}
