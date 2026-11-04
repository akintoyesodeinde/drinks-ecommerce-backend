namespace DrinksEcommerce.Domain.Enums;

public enum OrderStatus
{
    PendingPayment,
    Confirmed,
    Processing,
    ReadyForDispatch,
    OutForDelivery,
    Delivered,
    DeliveryFailed,
    CancellationRequested,
    Cancelled,
    RefundPending,
    PartiallyRefunded,
    Refunded
}

public enum PaymentStatus
{
    Initialized,
    Pending,
    Successful,
    Failed,
    Abandoned,
    Reversed,
    RefundPending,
    PartiallyRefunded,
    Refunded
}

public enum DeliveryStatus
{
    NotScheduled,
    AwaitingDispatch,
    Assigned,
    OutForDelivery,
    Delivered,
    FailedAttempt,
    Returned,
    Cancelled
}

public enum DeliveryMethod
{
    Delivery,
    Pickup
}

public enum PackType
{
    Single,
    Multipack,
    Carton,
    Crate,
    Sachet,
    Bundle
}

public enum StockStatus
{
    InStock,
    LowStock,
    OutOfStock
}

public enum RefundStatus
{
    Requested,
    Pending,
    Processing,
    Processed,
    Failed,
    NeedsAttention
}

public enum DiscountType
{
    Percentage,
    FixedAmount
}

public enum InventoryTransactionType
{
    InitialStock,
    Restock,
    Adjustment,
    Damage,
    Correction,
    Reservation,
    ReservationRelease,
    Sale,
    Return
}

public enum ReconciliationType
{
    PendingPaymentSucceededAtGateway,
    AmountMismatch,
    CurrencyMismatch,
    GatewayPaymentWithoutOrder,
    DuplicateReference,
    AmbiguousInitialization,
    RefundDiscrepancy,
    ReversedTransaction,
    RepeatedVerificationFailure,
    PaidButStockUnavailable
}

public enum ReconciliationStatus
{
    Open,
    Resolved
}
