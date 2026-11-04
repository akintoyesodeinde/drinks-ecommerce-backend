using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Enums;
using DrinksEcommerce.Domain.Products;

namespace DrinksEcommerce.Domain.Inventory;

public class InventoryItem : BaseEntity
{
    public Guid VariantId { get; private set; }
    public ProductVariant? Variant { get; private set; }
    public int OnHand { get; private set; }
    public int Reserved { get; private set; }
    public int LowStockThreshold { get; private set; } = 10;
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();

    public int Available => Math.Max(0, OnHand - Reserved);

    public StockStatus GetStockStatus()
    {
        if (Available <= 0)
        {
            return StockStatus.OutOfStock;
        }

        return Available <= LowStockThreshold ? StockStatus.LowStock : StockStatus.InStock;
    }

    private InventoryItem() { }

    public InventoryItem(Guid variantId, int initialOnHand, int lowStockThreshold = 10)
    {
        if (initialOnHand < 0)
        {
            throw new DomainException("Initial stock cannot be negative.", "validation_error", 400);
        }
        if (lowStockThreshold < 0)
        {
            throw new DomainException("Low stock threshold cannot be negative.", "validation_error", 400);
        }

        VariantId = variantId;
        OnHand = initialOnHand;
        Reserved = 0;
        LowStockThreshold = lowStockThreshold;
    }

    public InventoryTransaction Reserve(int quantity, Guid orderId, string? actor)
    {
        if (quantity <= 0)
        {
            throw new DomainException("Reservation quantity must be positive.", "validation_error", 400);
        }

        if (Available < quantity)
        {
            throw DomainException.Conflict(
                $"Insufficient stock for SKU {Variant?.Sku ?? VariantId.ToString()}. Requested {quantity}, available {Available}.",
                "insufficient_stock");
        }

        Reserved += quantity;
        BumpConcurrencyToken();
        return new InventoryTransaction(VariantId, InventoryTransactionType.Reservation, 0, OnHand, Reserved, $"Reserved {quantity} units for order.", orderId, actor);
    }

    public InventoryTransaction ReleaseReservation(int quantity, Guid orderId, string reason, string? actor)
    {
        if (quantity <= 0)
        {
            throw new DomainException("Release quantity must be positive.", "validation_error", 400);
        }

        var actualRelease = Math.Min(Reserved, quantity);
        Reserved -= actualRelease;
        BumpConcurrencyToken();
        return new InventoryTransaction(VariantId, InventoryTransactionType.ReservationRelease, 0, OnHand, Reserved, reason, orderId, actor);
    }

    public InventoryTransaction CommitReservedSale(int quantity, Guid orderId, string? actor)
    {
        if (quantity <= 0)
        {
            throw new DomainException("Sale quantity must be positive.", "validation_error", 400);
        }

        if (OnHand < quantity)
        {
            throw DomainException.Conflict("Cannot commit sale: on-hand stock is lower than committed quantity.", "insufficient_stock");
        }

        Reserved = Math.Max(0, Reserved - quantity);
        OnHand -= quantity;
        BumpConcurrencyToken();
        return new InventoryTransaction(VariantId, InventoryTransactionType.Sale, -quantity, OnHand, Reserved, $"Committed sale of {quantity} units.", orderId, actor);
    }

    public InventoryTransaction ReturnCommittedStock(int quantity, Guid orderId, string reason, string? actor)
    {
        if (quantity <= 0)
        {
            throw new DomainException("Return quantity must be positive.", "validation_error", 400);
        }

        OnHand += quantity;
        BumpConcurrencyToken();
        return new InventoryTransaction(VariantId, InventoryTransactionType.Return, quantity, OnHand, Reserved, reason, orderId, actor);
    }

    public InventoryTransaction Adjust(int quantityChange, InventoryTransactionType type, string reason, string? actor)
    {
        if (quantityChange == 0)
        {
            throw new DomainException("Stock adjustment quantity cannot be zero.", "validation_error", 400);
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("Reason is required for manual inventory adjustment.", "validation_error", 400);
        }

        var nextOnHand = OnHand + quantityChange;
        if (nextOnHand < 0)
        {
            throw DomainException.Conflict("Adjustment would result in negative on-hand stock.", "insufficient_stock");
        }
        if (nextOnHand < Reserved)
        {
            throw DomainException.Conflict(
                $"Cannot reduce on-hand stock ({nextOnHand}) below currently reserved stock ({Reserved}).",
                "insufficient_stock");
        }

        OnHand = nextOnHand;
        BumpConcurrencyToken();
        return new InventoryTransaction(VariantId, type, quantityChange, OnHand, Reserved, reason.Trim(), null, actor);
    }

    public void UpdateThreshold(int threshold)
    {
        if (threshold < 0 || threshold > 100000)
        {
            throw new DomainException("Low stock threshold must be between 0 and 100,000.", "validation_error", 400);
        }

        LowStockThreshold = threshold;
        BumpConcurrencyToken();
    }

    private void BumpConcurrencyToken()
    {
        RowVersion = Guid.NewGuid().ToByteArray();
        Touch();
    }
}

public class InventoryTransaction : BaseEntity
{
    public Guid VariantId { get; private set; }
    public InventoryTransactionType Type { get; private set; }
    public int QuantityChange { get; private set; }
    public int OnHandAfter { get; private set; }
    public int ReservedAfter { get; private set; }
    public string? Reason { get; private set; }
    public Guid? OrderId { get; private set; }
    public string? Actor { get; private set; }

    private InventoryTransaction() { }

    public InventoryTransaction(
        Guid variantId,
        InventoryTransactionType type,
        int quantityChange,
        int onHandAfter,
        int reservedAfter,
        string? reason,
        Guid? orderId,
        string? actor)
    {
        VariantId = variantId;
        Type = type;
        QuantityChange = quantityChange;
        OnHandAfter = onHandAfter;
        ReservedAfter = reservedAfter;
        Reason = reason;
        OrderId = orderId;
        Actor = actor;
    }
}

public enum StockReservationStatus
{
    Active,
    Committed,
    Released,
    Expired
}

public class StockReservation : BaseEntity
{
    public Guid OrderId { get; private set; }
    public StockReservationStatus Status { get; private set; } = StockReservationStatus.Active;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public ICollection<StockReservationItem> Items { get; private set; } = new List<StockReservationItem>();

    private StockReservation() { }

    public StockReservation(Guid orderId, DateTimeOffset expiresAt, IEnumerable<(Guid VariantId, int Quantity)> items)
    {
        OrderId = orderId;
        ExpiresAt = expiresAt;
        foreach (var (variantId, qty) in items)
        {
            Items.Add(new StockReservationItem(Id, variantId, qty));
        }
    }

    public bool TryMarkCommitted()
    {
        if (Status == StockReservationStatus.Committed)
        {
            return false;
        }
        if (Status is StockReservationStatus.Released or StockReservationStatus.Expired)
        {
            return false;
        }

        Status = StockReservationStatus.Committed;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
        return true;
    }

    public bool TryMarkReleased(bool expired = false)
    {
        if (Status != StockReservationStatus.Active)
        {
            return false;
        }

        Status = expired ? StockReservationStatus.Expired : StockReservationStatus.Released;
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
        return true;
    }
}

public class StockReservationItem : BaseEntity
{
    public Guid ReservationId { get; private set; }
    public Guid VariantId { get; private set; }
    public int Quantity { get; private set; }

    private StockReservationItem() { }

    public StockReservationItem(Guid reservationId, Guid variantId, int quantity)
    {
        ReservationId = reservationId;
        VariantId = variantId;
        Quantity = quantity;
    }
}
