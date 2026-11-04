using DrinksEcommerce.Application.Common;
using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Infrastructure.BackgroundJobs;
using DrinksEcommerce.Infrastructure.Notifications;
using DrinksEcommerce.Infrastructure.Payments.Paystack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DrinksEcommerce.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMemoryCache();

        var redisConn = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConn))
        {
            services.AddStackExchangeRedisCache(opts =>
            {
                opts.Configuration = redisConn;
                opts.InstanceName = "DrinksEcommerce:";
            });
        }
        else
        {
            services.AddDistributedMemoryCache();
        }

        services.AddSingleton<SimulatedPaymentStore>();

        var paystackSection = configuration.GetSection(PaystackOptions.SectionName);
        var baseUrl = paystackSection["BaseUrl"] ?? "https://api.paystack.co/";
        var timeoutSec = int.TryParse(paystackSection["TimeoutSeconds"], out var t) ? Math.Clamp(t, 5, 60) : 20;

        services.AddHttpClient<IPaymentGateway, PaystackPaymentGateway>(client =>
        {
            client.BaseAddress = new Uri(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/");
            client.Timeout = TimeSpan.FromSeconds(timeoutSec);
        });

        services.AddScoped<ICatalogCacheService, CatalogCacheService>();
        services.AddScoped<IAuditService, AuditService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<INotificationService>(sp => sp.GetRequiredService<NotificationService>());

        if (!string.Equals(configuration["BackgroundJobs:Disabled"], "true", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<OperationsBackgroundWorker>();
        }

        return services;
    }
}
