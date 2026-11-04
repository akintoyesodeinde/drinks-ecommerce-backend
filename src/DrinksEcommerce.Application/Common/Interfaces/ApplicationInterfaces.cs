using DrinksEcommerce.Domain.Customers;
using DrinksEcommerce.Domain.Delivery;
using DrinksEcommerce.Domain.Enums;
using DrinksEcommerce.Domain.Inventory;
using DrinksEcommerce.Domain.Operations;
using DrinksEcommerce.Domain.Orders;
using DrinksEcommerce.Domain.Payments;
using DrinksEcommerce.Domain.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace DrinksEcommerce.Application.Common.Interfaces;

public interface IApplicationDbContext
{
    DbSet<Category> Categories { get; }
    DbSet<Product> Products { get; }
    DbSet<ProductVariant> ProductVariants { get; }
    DbSet<ProductImage> ProductImages { get; }
    DbSet<ProductAttribute> ProductAttributes { get; }
    DbSet<InventoryItem> InventoryItems { get; }
    DbSet<InventoryTransaction> InventoryTransactions { get; }
    DbSet<StockReservation> StockReservations { get; }
    DbSet<StockReservationItem> StockReservationItems { get; }
    DbSet<CustomerAddress> CustomerAddresses { get; }
    DbSet<RefreshToken> RefreshTokens { get; }
    DbSet<Cart> Carts { get; }
    DbSet<CartItem> CartItems { get; }
    DbSet<Coupon> Coupons { get; }
    DbSet<Promotion> Promotions { get; }
    DbSet<DeliveryZone> DeliveryZones { get; }
    DbSet<DeliveryFeeRule> DeliveryFeeRules { get; }
    DbSet<PickupLocation> PickupLocations { get; }
    DbSet<OrderDelivery> OrderDeliveries { get; }
    DbSet<DeliveryStatusHistory> DeliveryStatusHistories { get; }
    DbSet<Order> Orders { get; }
    DbSet<OrderItem> OrderItems { get; }
    DbSet<OrderStatusHistory> OrderStatusHistories { get; }
    DbSet<Payment> Payments { get; }
    DbSet<PaymentAttempt> PaymentAttempts { get; }
    DbSet<PaymentStatusHistory> PaymentStatusHistories { get; }
    DbSet<PaymentWebhookEvent> PaymentWebhookEvents { get; }
    DbSet<Refund> Refunds { get; }
    DbSet<ReconciliationRecord> ReconciliationRecords { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<Notification> Notifications { get; }
    DbSet<ApplicationSetting> ApplicationSettings { get; }

    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface ICurrentUserService
{
    Guid? UserId { get; }
    string? Email { get; }
    string? IpAddress { get; }
    IReadOnlyList<string> Roles { get; }
    bool IsInRole(string role);
}

public interface ICatalogCacheService
{
    Task<T?> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan ttl, CancellationToken cancellationToken = default);
    Task InvalidateCatalogAsync(CancellationToken cancellationToken = default);
}

public interface IAuditService
{
    void Record(string action, string entityType, string? entityId, string? details = null);
}

public interface INotificationService
{
    Task QueueAsync(Guid userId, string email, string type, string title, string message, Guid? orderId = null, CancellationToken cancellationToken = default);
}

public sealed record PaymentInitializeRequest(
    string Reference,
    string CustomerEmail,
    long AmountKobo,
    string Currency,
    string CallbackUrl,
    Guid OrderId,
    string OrderNumber);

public sealed record PaymentInitializeResult(
    bool Succeeded,
    string Reference,
    string? AuthorizationUrl,
    string? AccessCode,
    string? ErrorMessage,
    bool IsAmbiguousTimeout = false);

public sealed record PaymentVerifyResult(
    bool Succeeded,
    string Reference,
    string ProviderStatus,
    long AmountKobo,
    string Currency,
    string? ProviderTransactionId,
    string? Channel,
    string? GatewayResponse,
    DateTimeOffset? PaidAtUtc,
    Guid? MetadataOrderId,
    string? ErrorMessage);

public sealed record RefundGatewayRequest(
    string TransactionReference,
    long AmountKobo,
    string Currency,
    string Reason);

public sealed record RefundGatewayResult(
    bool Succeeded,
    RefundStatus Status,
    string? ProviderRefundId,
    string? Message);

public sealed record ParsedWebhookEvent(
    string EventType,
    string DedupKey,
    string? Reference,
    long? AmountKobo,
    string? Currency,
    string? ProviderStatus,
    string? ProviderTransactionId,
    string? Channel,
    string? GatewayResponse,
    string? ProviderRefundId);

public interface IPaymentGateway
{
    Task<PaymentInitializeResult> InitializePaymentAsync(PaymentInitializeRequest request, CancellationToken cancellationToken = default);
    Task<PaymentVerifyResult> VerifyPaymentAsync(string reference, CancellationToken cancellationToken = default);
    Task<RefundGatewayResult> RequestRefundAsync(RefundGatewayRequest request, CancellationToken cancellationToken = default);
    bool VerifyWebhookSignature(string rawPayload, string? signatureHeader);
    ParsedWebhookEvent ParseWebhookEvent(string rawPayload);
}
