using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Enums;

namespace DrinksEcommerce.Domain.Orders;

public class OrderAddressSnapshot
{
    public string RecipientName { get; private set; } = string.Empty;
    public string PhoneNumber { get; private set; } = string.Empty;
    public string State { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public string Street { get; private set; } = string.Empty;
    public string? Landmark { get; private set; }
    public string? PostalCode { get; private set; }

    private OrderAddressSnapshot() { }

    public OrderAddressSnapshot(
        string recipientName,
        string phoneNumber,
        string state,
        string city,
        string street,
        string? landmark,
        string? postalCode)
    {
        RecipientName = recipientName;
        PhoneNumber = phoneNumber;
        State = state;
        City = city;
        Street = street;
        Landmark = landmark;
        PostalCode = postalCode;
    }

    public string FormatSingleLine() =>
        string.Join(", ", new[] { Street, Landmark, City, State, PostalCode }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public class Order : BaseEntity
{
    public string OrderNumber { get; private set; } = string.Empty;
    public Guid UserId { get; private set; }
    public string ContactEmail { get; private set; } = string.Empty;
    public string CustomerName { get; private set; } = string.Empty;
    public string? CustomerPhone { get; private set; }
    public OrderStatus Status { get; private set; } = OrderStatus.PendingPayment;
    public OrderStatus? PreviousStatusBeforeHold { get; private set; }
    public DeliveryMethod DeliveryMethod { get; private set; } = DeliveryMethod.Delivery;
    public OrderAddressSnapshot? AddressSnapshot { get; private set; }
    public Guid? PickupLocationId { get; private set; }
    public string? DeliveryInstructions { get; private set; }
    public string? CouponCode { get; private set; }

    public decimal Subtotal { get; private set; }
    public decimal Discount { get; private set; }
    public decimal DeliveryFee { get; private set; }
    public decimal Tax { get; private set; }
    public decimal Total { get; private set; }
    public string Currency { get; private set; } = NigerianValueRules.DefaultCurrency;

    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTimeOffset? ReservationExpiresAt { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();

    public ICollection<OrderItem> Items { get; private set; } = new List<OrderItem>();
    public ICollection<OrderStatusHistory> StatusHistory { get; private set; } = new List<OrderStatusHistory>();

    private Order() { }

    public Order(
        string orderNumber,
        Guid userId,
        string customerName,
        string contactEmail,
        string? customerPhone,
        DeliveryMethod deliveryMethod,
        OrderAddressSnapshot? addressSnapshot,
        Guid? pickupLocationId,
        string? deliveryInstructions,
        string? couponCode,
        decimal subtotal,
        decimal discount,
        decimal deliveryFee,
        decimal tax,
        string idempotencyKey,
        DateTimeOffset reservationExpiresAt,
        IEnumerable<OrderItem> items)
    {
        OrderNumber = orderNumber;
        UserId = userId;
        CustomerName = customerName.Trim();
        ContactEmail = contactEmail.Trim();
        CustomerPhone = customerPhone;
        DeliveryMethod = deliveryMethod;
        AddressSnapshot = addressSnapshot;
        PickupLocationId = pickupLocationId;
        DeliveryInstructions = string.IsNullOrWhiteSpace(deliveryInstructions) ? null : deliveryInstructions.Trim();
        CouponCode = couponCode;
        Subtotal = NigerianValueRules.RoundNaira(subtotal);
        Discount = NigerianValueRules.RoundNaira(discount);
        DeliveryFee = NigerianValueRules.RoundNaira(deliveryFee);
        Tax = NigerianValueRules.RoundNaira(tax);
        Total = NigerianValueRules.RoundNaira(Math.Max(0m, Subtotal - Discount + DeliveryFee + Tax));
        Currency = NigerianValueRules.DefaultCurrency;
        IdempotencyKey = idempotencyKey;
        ReservationExpiresAt = reservationExpiresAt;
        Status = OrderStatus.PendingPayment;

        foreach (var item in items)
        {
            Items.Add(item);
        }

        StatusHistory.Add(new OrderStatusHistory(Id, OrderStatus.PendingPayment, "Order created and awaiting payment verification.", customerName));
    }

    public bool CanCustomerCancel =>
        Status is OrderStatus.PendingPayment or OrderStatus.Confirmed or OrderStatus.Processing;

    public bool CanPay =>
        Status == OrderStatus.PendingPayment;

    public OrderStatusHistory? MarkConfirmedByVerifiedPayment(string reference, string? actor = "PaystackVerifier")
    {
        if (Status is OrderStatus.Confirmed or OrderStatus.Processing or OrderStatus.ReadyForDispatch or OrderStatus.OutForDelivery or OrderStatus.Delivered)
        {
            return null;
        }
        if (Status != OrderStatus.PendingPayment && Status != OrderStatus.Cancelled)
        {
            throw DomainException.Conflict($"Order {OrderNumber} cannot transition from {Status} to Confirmed.", "invalid_state_transition");
        }

        Status = OrderStatus.Confirmed;
        ConfirmedAt = DateTimeOffset.UtcNow;
        ReservationExpiresAt = null;
        return RecordTransition(OrderStatus.Confirmed, $"Payment verified (ref: {reference}). Order confirmed for fulfilment.", actor);
    }

    public OrderStatusHistory TransitionTo(OrderStatus next, string? note, string? actor)
    {
        if (!IsValidTransition(Status, next))
        {
            throw DomainException.Conflict(
                $"Invalid order state transition from {Status} to {next}.",
                "invalid_state_transition");
        }

        if (next == OrderStatus.Delivered && DeliveryMethod == DeliveryMethod.Pickup && Status != OrderStatus.ReadyForDispatch)
        {
            throw DomainException.Conflict("Pickup orders must be ReadyForDispatch before marking Delivered.", "invalid_state_transition");
        }

        if (next is OrderStatus.CancellationRequested or OrderStatus.RefundPending)
        {
            PreviousStatusBeforeHold = Status;
        }

        Status = next;
        if (next == OrderStatus.Delivered)
        {
            DeliveredAt = DateTimeOffset.UtcNow;
        }
        else if (next == OrderStatus.Cancelled)
        {
            CancelledAt = DateTimeOffset.UtcNow;
            ReservationExpiresAt = null;
        }

        return RecordTransition(next, note, actor);
    }

    public OrderStatusHistory RejectCancellation(string? note, string? actor)
    {
        if (Status != OrderStatus.CancellationRequested)
        {
            throw DomainException.Conflict("Order does not have a pending cancellation request.", "invalid_state_transition");
        }

        var restored = PreviousStatusBeforeHold ?? OrderStatus.Confirmed;
        PreviousStatusBeforeHold = null;
        Status = restored;
        return RecordTransition(restored, string.IsNullOrWhiteSpace(note) ? "Cancellation request rejected." : note.Trim(), actor);
    }

    public static bool IsValidTransition(OrderStatus current, OrderStatus next) => (current, next) switch
    {
        (OrderStatus.PendingPayment, OrderStatus.Confirmed) => true,
        (OrderStatus.PendingPayment, OrderStatus.Cancelled) => true,

        (OrderStatus.Confirmed, OrderStatus.Processing) => true,
        (OrderStatus.Confirmed, OrderStatus.CancellationRequested) => true,
        (OrderStatus.Confirmed, OrderStatus.RefundPending) => true,

        (OrderStatus.Processing, OrderStatus.ReadyForDispatch) => true,
        (OrderStatus.Processing, OrderStatus.CancellationRequested) => true,
        (OrderStatus.Processing, OrderStatus.RefundPending) => true,

        (OrderStatus.ReadyForDispatch, OrderStatus.OutForDelivery) => true,
        (OrderStatus.ReadyForDispatch, OrderStatus.Delivered) => true,
        (OrderStatus.ReadyForDispatch, OrderStatus.CancellationRequested) => true,
        (OrderStatus.ReadyForDispatch, OrderStatus.RefundPending) => true,

        (OrderStatus.OutForDelivery, OrderStatus.Delivered) => true,
        (OrderStatus.OutForDelivery, OrderStatus.DeliveryFailed) => true,

        (OrderStatus.DeliveryFailed, OrderStatus.OutForDelivery) => true,
        (OrderStatus.DeliveryFailed, OrderStatus.ReadyForDispatch) => true,
        (OrderStatus.DeliveryFailed, OrderStatus.RefundPending) => true,
        (OrderStatus.DeliveryFailed, OrderStatus.Cancelled) => true,

        (OrderStatus.CancellationRequested, OrderStatus.Cancelled) => true,
        (OrderStatus.CancellationRequested, OrderStatus.RefundPending) => true,

        (OrderStatus.Delivered, OrderStatus.RefundPending) => true,

        (OrderStatus.RefundPending, OrderStatus.PartiallyRefunded) => true,
        (OrderStatus.RefundPending, OrderStatus.Refunded) => true,
        (OrderStatus.RefundPending, OrderStatus.Confirmed) => true,

        (OrderStatus.PartiallyRefunded, OrderStatus.RefundPending) => true,
        (OrderStatus.PartiallyRefunded, OrderStatus.Refunded) => true,

        _ => false
    };

    private OrderStatusHistory RecordTransition(OrderStatus status, string? note, string? actor)
    {
        RowVersion = Guid.NewGuid().ToByteArray();
        Touch();
        var history = new OrderStatusHistory(Id, status, note, actor);
        StatusHistory.Add(history);
        return history;
    }
}

public class OrderItem : BaseEntity
{
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public Guid VariantId { get; private set; }
    public string ProductName { get; private set; } = string.Empty;
    public string VariantName { get; private set; } = string.Empty;
    public string Sku { get; private set; } = string.Empty;
    public string? ImageUrl { get; private set; }
    public decimal UnitPrice { get; private set; }
    public int Quantity { get; private set; }
    public decimal LineTotal { get; private set; }

    private OrderItem() { }

    public OrderItem(
        Guid productId,
        Guid variantId,
        string productName,
        string variantName,
        string sku,
        string? imageUrl,
        decimal unitPrice,
        int quantity)
    {
        ProductId = productId;
        VariantId = variantId;
        ProductName = productName;
        VariantName = variantName;
        Sku = sku;
        ImageUrl = imageUrl;
        UnitPrice = NigerianValueRules.RoundNaira(unitPrice);
        Quantity = quantity;
        LineTotal = NigerianValueRules.RoundNaira(UnitPrice * quantity);
    }
}

public class OrderStatusHistory : BaseEntity
{
    public Guid OrderId { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public string? Note { get; private set; }
    public string? Actor { get; private set; }

    private OrderStatusHistory() { }

    public OrderStatusHistory(Guid orderId, OrderStatus status, string? note, string? actor)
    {
        OrderId = orderId;
        Status = status.ToString();
        Note = note;
        Actor = actor;
    }
}
