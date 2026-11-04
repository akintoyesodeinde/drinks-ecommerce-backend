using DrinksEcommerce.Domain.Enums;

namespace DrinksEcommerce.Application.Contracts;

// Auth & Account
public sealed record RegisterRequest(string FirstName, string LastName, string Email, string PhoneNumber, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record ForgotPasswordRequest(string Email);
public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);
public sealed record ConfirmEmailRequest(string UserId, string Token);
public sealed record UpdateProfileRequest(string FirstName, string LastName, string PhoneNumber);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record UserDto(
    string Id,
    string Email,
    string FirstName,
    string LastName,
    string? PhoneNumber,
    bool EmailConfirmed,
    IReadOnlyList<string> Roles);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserDto User);

// Addresses
public sealed record AddressInput(
    string? Label,
    string RecipientName,
    string PhoneNumber,
    string State,
    string City,
    string Street,
    string? Landmark,
    string? PostalCode,
    string? DeliveryInstructions,
    bool? IsDefault);

public sealed record AddressDto(
    string Id,
    string? Label,
    string RecipientName,
    string PhoneNumber,
    string State,
    string City,
    string Street,
    string? Landmark,
    string? PostalCode,
    string? DeliveryInstructions,
    bool IsDefault,
    bool IsServiceable);

// Catalog
public sealed record VariantDto(
    string Id,
    string Sku,
    string Name,
    PackType PackType,
    int UnitsPerPack,
    int? UnitVolumeMl,
    decimal Price,
    decimal? CompareAtPrice,
    StockStatus StockStatus,
    int AvailableQuantity,
    bool IsActive);

public sealed record ProductImageDto(string Id, string Url, string? AltText, int SortOrder);
public sealed record ProductAttributeDto(string Name, string Value);

public sealed record ProductSummaryDto(
    string Id,
    string Slug,
    string Name,
    string Brand,
    string CategoryId,
    string CategoryName,
    string CategorySlug,
    string? ImageUrl,
    decimal MinPrice,
    decimal MaxPrice,
    bool InStock,
    bool IsAlcoholic,
    bool IsFeatured,
    int VariantCount,
    VariantDto? DefaultVariant);

public sealed record ProductDetailDto(
    string Id,
    string Slug,
    string Name,
    string Description,
    string Brand,
    string CategoryId,
    string CategoryName,
    string CategorySlug,
    bool IsAlcoholic,
    bool IsFeatured,
    IReadOnlyList<ProductImageDto> Images,
    IReadOnlyList<ProductAttributeDto> Attributes,
    IReadOnlyList<VariantDto> Variants);

public sealed record CategoryDto(
    string Id,
    string Name,
    string Slug,
    string? Description,
    string? ImageUrl,
    int ProductCount);

public sealed record PromotionDto(
    string Id,
    string Title,
    string Description,
    string? ImageUrl,
    string? LinkUrl,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt);

// Cart
public sealed record AddCartItemRequest(Guid VariantId, int Quantity);
public sealed record UpdateCartItemRequest(int Quantity);
public sealed record ApplyCouponRequest(string Code);
public sealed record MergeCartRequest(IReadOnlyList<AddCartItemRequest> Items);

public sealed record CartItemDto(
    string Id,
    string ProductId,
    string ProductName,
    string ProductSlug,
    string VariantId,
    string VariantName,
    string Sku,
    string? ImageUrl,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal,
    int AvailableQuantity,
    StockStatus StockStatus,
    bool IsAvailable);

public sealed record CartDto(
    string Id,
    IReadOnlyList<CartItemDto> Items,
    int ItemCount,
    decimal Subtotal,
    decimal Discount,
    string? CouponCode,
    decimal Total,
    IReadOnlyList<string> Warnings);

// Delivery
public sealed record DeliveryZoneDto(
    string Id,
    string Name,
    string State,
    IReadOnlyList<string> Cities,
    decimal BaseFee,
    decimal? FreeDeliveryThreshold,
    int EstimatedMinDays,
    int EstimatedMaxDays);

public sealed record DeliveryEstimateDto(
    bool Serviceable,
    string? ZoneId,
    string? ZoneName,
    decimal Fee,
    bool FreeDeliveryApplied,
    decimal? FreeDeliveryThreshold,
    int? EstimatedMinDays,
    int? EstimatedMaxDays,
    string Message);

public sealed record PickupLocationDto(
    string Id,
    string Name,
    string Address,
    string State,
    string City,
    string OpeningHours,
    string PhoneNumber);

// Checkout & Payments
public sealed record CheckoutRequest(
    DeliveryMethod DeliveryMethod,
    Guid? AddressId,
    AddressInput? Address,
    bool? SaveAddress,
    Guid? PickupLocationId,
    string? DeliveryInstructions,
    string? ContactEmail,
    string? Client);

public sealed record CheckoutPreviewDto(
    IReadOnlyList<CartItemDto> Items,
    decimal Subtotal,
    decimal Discount,
    decimal DeliveryFee,
    decimal Tax,
    decimal Total,
    string Currency,
    string? CouponCode,
    bool Serviceable,
    string? Message,
    int? EstimatedMinDays,
    int? EstimatedMaxDays,
    IReadOnlyList<string> Warnings);

public sealed record CheckoutResultDto(
    string OrderId,
    string OrderNumber,
    decimal Total,
    string Currency,
    string PaymentReference,
    string? AuthorizationUrl,
    PaymentStatus PaymentStatus,
    DateTimeOffset ReservationExpiresAt,
    string? Message);

public sealed record InitializePaymentForOrderRequest(Guid OrderId, string? Client);

public sealed record PaymentStatusDto(
    string Reference,
    string OrderId,
    string OrderNumber,
    PaymentStatus Status,
    decimal Amount,
    string Currency,
    DateTimeOffset? PaidAt,
    string? Channel,
    OrderStatus OrderStatus,
    string Message);

// Orders & Tracking
public sealed record CancelOrderRequest(string? Reason);

public sealed record OrderSummaryDto(
    string Id,
    string OrderNumber,
    OrderStatus Status,
    PaymentStatus? PaymentStatus,
    DeliveryMethod DeliveryMethod,
    decimal Total,
    string Currency,
    int ItemCount,
    DateTimeOffset CreatedAt);

public sealed record OrderItemDto(
    string Id,
    string ProductId,
    string VariantId,
    string ProductName,
    string VariantName,
    string Sku,
    string? ImageUrl,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal);

public sealed record OrderAddressDto(
    string RecipientName,
    string PhoneNumber,
    string State,
    string City,
    string Street,
    string? Landmark,
    string? PostalCode);

public sealed record StatusHistoryDto(
    string Status,
    string? Note,
    string? Actor,
    DateTimeOffset CreatedAt);

public sealed record OrderPaymentSummaryDto(
    string Reference,
    PaymentStatus Status,
    decimal Amount,
    DateTimeOffset? PaidAt,
    string? Channel);

public sealed record OrderDetailDto(
    string Id,
    string OrderNumber,
    OrderStatus Status,
    PaymentStatus? PaymentStatus,
    DeliveryStatus? DeliveryStatus,
    DeliveryMethod DeliveryMethod,
    IReadOnlyList<OrderItemDto> Items,
    decimal Subtotal,
    decimal Discount,
    decimal DeliveryFee,
    decimal Tax,
    decimal Total,
    string Currency,
    string? CouponCode,
    OrderAddressDto? Address,
    PickupLocationDto? PickupLocation,
    string? DeliveryInstructions,
    string ContactEmail,
    OrderPaymentSummaryDto? Payment,
    IReadOnlyList<StatusHistoryDto> StatusHistory,
    bool CanCancel,
    bool CanPay,
    DateTimeOffset? ReservationExpiresAt,
    DateTimeOffset CreatedAt);

public sealed record RiderInfoDto(string Name, string PhoneNumber);

public sealed record TrackingStepDto(
    string Key,
    string Label,
    bool Completed,
    bool Current,
    DateTimeOffset? OccurredAt);

public sealed record TrackingDto(
    string OrderId,
    string OrderNumber,
    OrderStatus OrderStatus,
    DeliveryStatus? DeliveryStatus,
    DeliveryMethod DeliveryMethod,
    DateTimeOffset? EstimatedDeliveryFrom,
    DateTimeOffset? EstimatedDeliveryTo,
    RiderInfoDto? Rider,
    IReadOnlyList<TrackingStepDto> Steps,
    IReadOnlyList<StatusHistoryDto> History);

// Notifications
public sealed record NotificationDto(
    string Id,
    string Type,
    string Title,
    string Message,
    string? OrderId,
    bool IsRead,
    DateTimeOffset CreatedAt);

// Administration DTOs
public sealed record VariantInput(
    string Sku,
    string Name,
    PackType PackType,
    int UnitsPerPack,
    int? UnitVolumeMl,
    decimal Price,
    decimal? CompareAtPrice,
    bool IsActive,
    int LowStockThreshold,
    int? InitialStock);

public sealed record CreateProductRequest(
    string Name,
    string Description,
    string Brand,
    Guid CategoryId,
    bool IsAlcoholic,
    bool IsFeatured,
    bool IsActive,
    IReadOnlyList<ProductAttributeDto>? Attributes,
    IReadOnlyList<VariantInput> Variants);

public sealed record UpdateProductRequest(
    string Name,
    string Description,
    string Brand,
    Guid CategoryId,
    bool IsAlcoholic,
    bool IsFeatured,
    bool IsActive,
    IReadOnlyList<ProductAttributeDto>? Attributes);

public sealed record AdminVariantDto(
    string Id,
    string Sku,
    string Name,
    PackType PackType,
    int UnitsPerPack,
    int? UnitVolumeMl,
    decimal Price,
    decimal? CompareAtPrice,
    StockStatus StockStatus,
    int AvailableQuantity,
    bool IsActive,
    int OnHand,
    int Reserved,
    int Available,
    int LowStockThreshold);

public sealed record AdminProductSummaryDto(
    string Id,
    string Slug,
    string Name,
    string Brand,
    string CategoryName,
    string? ImageUrl,
    bool IsActive,
    bool IsAlcoholic,
    bool IsFeatured,
    int VariantCount,
    decimal MinPrice,
    decimal MaxPrice,
    int TotalAvailable,
    DateTimeOffset UpdatedAt);

public sealed record AdminProductDto(
    string Id,
    string Slug,
    string Name,
    string Description,
    string Brand,
    string CategoryId,
    string CategoryName,
    string CategorySlug,
    bool IsAlcoholic,
    bool IsFeatured,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ProductImageDto> Images,
    IReadOnlyList<ProductAttributeDto> Attributes,
    IReadOnlyList<AdminVariantDto> Variants);

public sealed record CategoryInput(string Name, string? Description, bool IsActive, int SortOrder, string? ImageUrl = null);
public sealed record AdminCategoryDto(
    string Id,
    string Name,
    string Slug,
    string? Description,
    string? ImageUrl,
    int ProductCount,
    bool IsActive,
    int SortOrder);

public sealed record AdjustInventoryRequest(int QuantityChange, InventoryTransactionType Type, string Reason);
public sealed record UpdateThresholdRequest(int LowStockThreshold);

public sealed record InventoryItemDto(
    string VariantId,
    string ProductId,
    string ProductName,
    string VariantName,
    string Sku,
    int OnHand,
    int Reserved,
    int Available,
    int LowStockThreshold,
    bool IsLowStock,
    DateTimeOffset UpdatedAt);

public sealed record InventoryTransactionDto(
    string Id,
    InventoryTransactionType Type,
    int QuantityChange,
    int OnHandAfter,
    int ReservedAfter,
    string? Reason,
    string? OrderId,
    string? Actor,
    DateTimeOffset CreatedAt);

public sealed record UpdateOrderStatusRequest(OrderStatus Status, string? Note);
public sealed record OrderNoteRequest(string? Note);

public sealed record AdminOrderSummaryDto(
    string Id,
    string OrderNumber,
    OrderStatus Status,
    PaymentStatus? PaymentStatus,
    DeliveryMethod DeliveryMethod,
    decimal Total,
    string Currency,
    int ItemCount,
    DateTimeOffset CreatedAt,
    string CustomerName,
    string CustomerEmail);

public sealed record DeliveryDto(
    string Id,
    string OrderId,
    string OrderNumber,
    DeliveryStatus Status,
    OrderStatus OrderStatus,
    string RecipientName,
    string PhoneNumber,
    string AddressLine,
    string State,
    string City,
    string? DeliveryInstructions,
    string? AssignedToUserId,
    string? RiderName,
    string? RiderPhone,
    DateTimeOffset? ScheduledFor,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? DeliveredAt,
    string? ReceivedBy,
    int AttemptCount,
    string? LastException,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<StatusHistoryDto> History);

public sealed record AdminOrderDetailDto(
    string Id,
    string OrderNumber,
    OrderStatus Status,
    PaymentStatus? PaymentStatus,
    DeliveryStatus? DeliveryStatus,
    DeliveryMethod DeliveryMethod,
    IReadOnlyList<OrderItemDto> Items,
    decimal Subtotal,
    decimal Discount,
    decimal DeliveryFee,
    decimal Tax,
    decimal Total,
    string Currency,
    string? CouponCode,
    OrderAddressDto? Address,
    PickupLocationDto? PickupLocation,
    string? DeliveryInstructions,
    string ContactEmail,
    OrderPaymentSummaryDto? Payment,
    IReadOnlyList<StatusHistoryDto> StatusHistory,
    bool CanCancel,
    bool CanPay,
    DateTimeOffset? ReservationExpiresAt,
    DateTimeOffset CreatedAt,
    string CustomerId,
    string CustomerName,
    string CustomerEmail,
    string? CustomerPhone,
    DeliveryDto? Delivery,
    IReadOnlyList<string> AllowedActions,
    decimal RefundedAmount,
    decimal RefundableAmount);

public sealed record DeliveryStaffDto(string Id, string Name, string Email, string? PhoneNumber);
public sealed record AssignDeliveryRequest(Guid? AssignedToUserId, string RiderName, string RiderPhone, DateTimeOffset? ScheduledFor);
public sealed record DispatchDeliveryRequest(string? Note);
public sealed record CompleteDeliveryRequest(string? ReceivedBy, string? Note);
public sealed record DeliveryExceptionRequest(string Reason, string? Note);

public sealed record PaymentAttemptDto(string Id, string Reference, string Status, string? AuthorizationUrl, string? ErrorMessage, DateTimeOffset CreatedAt);

public sealed record RefundDto(
    string Id,
    string PaymentId,
    string PaymentReference,
    string OrderId,
    string OrderNumber,
    decimal Amount,
    string Currency,
    RefundStatus Status,
    string Reason,
    string? ProviderRefundId,
    string? RequestedBy,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt);

public sealed record AdminPaymentDto(
    string Id,
    string Reference,
    string OrderId,
    string OrderNumber,
    PaymentStatus Status,
    decimal Amount,
    string Currency,
    string? Channel,
    DateTimeOffset? PaidAt,
    DateTimeOffset CreatedAt,
    decimal RefundedAmount,
    string CustomerEmail,
    int AttemptCount);

public sealed record AdminPaymentDetailDto(
    string Id,
    string Reference,
    string OrderId,
    string OrderNumber,
    PaymentStatus Status,
    decimal Amount,
    string Currency,
    string? Channel,
    DateTimeOffset? PaidAt,
    DateTimeOffset CreatedAt,
    decimal RefundedAmount,
    string CustomerEmail,
    int AttemptCount,
    string? GatewayResponse,
    string? ProviderTransactionId,
    IReadOnlyList<PaymentAttemptDto> Attempts,
    IReadOnlyList<RefundDto> Refunds,
    IReadOnlyList<StatusHistoryDto> History);

public sealed record CreateRefundRequest(Guid OrderId, decimal Amount, string Reason);

public sealed record ReconciliationRecordDto(
    string Id,
    ReconciliationType Type,
    ReconciliationStatus Status,
    string? PaymentReference,
    string? OrderId,
    string? OrderNumber,
    decimal? ExpectedAmount,
    decimal? ActualAmount,
    string? Currency,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string? ResolvedBy,
    string? ResolutionNote);

public sealed record ResolveReconciliationRequest(string Note);
public sealed record ReconciliationRunResultDto(int PaymentsChecked, int ExceptionsCreated);

public sealed record CouponInput(
    string Code,
    string? Description,
    DiscountType DiscountType,
    decimal Value,
    decimal? MinOrderAmount,
    decimal? MaxDiscountAmount,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    int? UsageLimit,
    int? PerCustomerLimit,
    bool IsActive);

public sealed record CouponDto(
    string Id,
    string Code,
    string? Description,
    DiscountType DiscountType,
    decimal Value,
    decimal? MinOrderAmount,
    decimal? MaxDiscountAmount,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    int? UsageLimit,
    int? PerCustomerLimit,
    bool IsActive,
    int UsedCount);

public sealed record DeliveryFeeRuleInput(decimal MinOrderAmount, decimal? MaxOrderAmount, decimal Fee);

public sealed record DeliveryZoneInput(
    string Name,
    string State,
    IReadOnlyList<string> Cities,
    decimal BaseFee,
    decimal? FreeDeliveryThreshold,
    int EstimatedMinDays,
    int EstimatedMaxDays,
    bool IsActive,
    IReadOnlyList<DeliveryFeeRuleInput>? FeeRules);

public sealed record AdminDeliveryZoneDto(
    string Id,
    string Name,
    string State,
    IReadOnlyList<string> Cities,
    decimal BaseFee,
    decimal? FreeDeliveryThreshold,
    int EstimatedMinDays,
    int EstimatedMaxDays,
    bool IsActive,
    IReadOnlyList<DeliveryFeeRuleInput> FeeRules);

public sealed record StatusCountDto(string Status, int Count);
public sealed record TopProductSalesDto(string ProductName, string VariantName, string Sku, int Quantity, decimal Revenue);
public sealed record DailySalesDto(string Date, int Orders, decimal Revenue);

public sealed record SalesSummaryDto(
    string From,
    string To,
    decimal GrossRevenue,
    decimal RefundedAmount,
    decimal NetRevenue,
    int PaidOrders,
    decimal AverageOrderValue,
    int PendingPaymentOrders,
    IReadOnlyList<StatusCountDto> OrdersByStatus,
    IReadOnlyList<TopProductSalesDto> TopProducts,
    IReadOnlyList<DailySalesDto> DailySales,
    int LowStockCount,
    int OpenReconciliationCount);

public sealed record AuditLogDto(
    string Id,
    string? Actor,
    string Action,
    string EntityType,
    string? EntityId,
    string? Details,
    string? IpAddress,
    DateTimeOffset CreatedAt);
