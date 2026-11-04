using DrinksEcommerce.Domain.Enums;

namespace DrinksEcommerce.Application.Common;

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages)
{
    public static PagedResult<T> Create(IReadOnlyList<T> items, int page, int pageSize, int totalCount)
    {
        var safePageSize = Math.Clamp(pageSize, 1, 100);
        var safePage = Math.Max(1, page);
        var totalPages = totalCount == 0 ? 0 : (int)Math.Ceiling(totalCount / (double)safePageSize);
        return new PagedResult<T>(items, safePage, safePageSize, totalCount, totalPages);
    }
}

public sealed class StoreOptions
{
    public const string SectionName = "Store";
    public string Name { get; set; } = "DrinksEcommerce Nigeria";
    public string Currency { get; set; } = "NGN";
    public string TimeZone { get; set; } = "Africa/Lagos";
    public bool AlcoholSalesEnabled { get; set; }
    public bool PickupEnabled { get; set; } = true;
    public decimal VatRatePercent { get; set; }
    public int ReservationExpiryMinutes { get; set; } = 30;
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "DrinksEcommerce.API";
    public string Audience { get; set; } = "DrinksEcommerce.Clients";
    public string SigningKey { get; set; } = "DevOnly_ChangeInProduction_DrinksEcommerce_SigningKey_2026_AtLeast32Bytes!";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 14;
}

public sealed class PaystackOptions
{
    public const string SectionName = "Payments";
    /// <summary>
    /// "Paystack" for live HTTP calls to api.paystack.co (using test or live keys),
    /// or "Simulated" for local/CI deterministic test simulator when no Paystack key is supplied.
    /// </summary>
    public string Provider { get; set; } = "Simulated";
    public string BaseUrl { get; set; } = "https://api.paystack.co/";
    public string SecretKey { get; set; } = "sk_test_simulated_drinksecommerce_key_do_not_use_in_prod";
    public string PublicKey { get; set; } = "pk_test_simulated_drinksecommerce_key";
    public string WebCallbackUrl { get; set; } = "http://localhost:5173/payment/verify";
    public string MobileCallbackUrl { get; set; } = "drinksecommerce://payment/verify";
    public string SimulatorBaseUrl { get; set; } = "http://localhost:5080";
    public int TimeoutSeconds { get; set; } = 20;
    public bool VerifyBeforeWebhookCommit { get; set; } = true;
}

public sealed class EmailOptions
{
    public const string SectionName = "Email";
    /// <summary>
    /// "DevelopmentLog" (safe dev sink that logs sanitized metadata without claiming external delivery) or "Smtp".
    /// </summary>
    public string Provider { get; set; } = "DevelopmentLog";
    public string FromAddress { get; set; } = "no-reply@drinksecommerce.local";
    public string FromName { get; set; } = "DrinksEcommerce Nigeria";
    public string SmtpHost { get; set; } = "localhost";
    public int SmtpPort { get; set; } = 1025;
    public bool UseSsl { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
}
