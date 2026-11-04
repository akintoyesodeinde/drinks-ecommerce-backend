using DrinksEcommerce.Application.Checkout;
using DrinksEcommerce.Application.Common;
using DrinksEcommerce.Application.Orders;
using DrinksEcommerce.Application.Payments;
using DrinksEcommerce.Application.Products;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DrinksEcommerce.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StoreOptions>(configuration.GetSection(StoreOptions.SectionName));
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<PaystackOptions>(configuration.GetSection(PaystackOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));

        services.AddScoped<CatalogService>();
        services.AddScoped<CartAndCheckoutService>();
        services.AddScoped<PaymentOrchestratorService>();
        services.AddScoped<OrderAndAdminService>();

        return services;
    }
}
