using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Enums;

namespace DrinksEcommerce.Domain.Customers;

public class CustomerAddress : BaseEntity
{
    public Guid UserId { get; private set; }
    public string? Label { get; private set; }
    public string RecipientName { get; private set; } = string.Empty;
    public string PhoneNumber { get; private set; } = string.Empty;
    public string State { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public string Street { get; private set; } = string.Empty;
    public string? Landmark { get; private set; }
    public string? PostalCode { get; private set; }
    public string? DeliveryInstructions { get; private set; }
    public bool IsDefault { get; private set; }

    private CustomerAddress() { }

    public CustomerAddress(
        Guid userId,
        string? label,
        string recipientName,
        string phoneNumber,
        string state,
        string city,
        string street,
        string? landmark,
        string? postalCode,
        string? deliveryInstructions,
        bool isDefault)
    {
        UserId = userId;
        Update(label, recipientName, phoneNumber, state, city, street, landmark, postalCode, deliveryInstructions, isDefault);
    }

    public void Update(
        string? label,
        string recipientName,
        string phoneNumber,
        string state,
        string city,
        string street,
        string? landmark,
        string? postalCode,
        string? deliveryInstructions,
        bool isDefault)
    {
        if (string.IsNullOrWhiteSpace(recipientName))
        {
            throw new DomainException("Recipient name is required.", "validation_error", 400);
        }
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new DomainException("State is required.", "validation_error", 400);
        }
        if (string.IsNullOrWhiteSpace(city))
        {
            throw new DomainException("City or LGA is required.", "validation_error", 400);
        }
        if (string.IsNullOrWhiteSpace(street))
        {
            throw new DomainException("Street address is required.", "validation_error", 400);
        }

        Label = string.IsNullOrWhiteSpace(label) ? "Home" : label.Trim();
        RecipientName = recipientName.Trim();
        PhoneNumber = NigerianValueRules.NormalizeNigerianPhoneOrThrow(phoneNumber);
        State = state.Trim();
        City = city.Trim();
        Street = street.Trim();
        Landmark = string.IsNullOrWhiteSpace(landmark) ? null : landmark.Trim();
        PostalCode = string.IsNullOrWhiteSpace(postalCode) ? null : postalCode.Trim();
        DeliveryInstructions = string.IsNullOrWhiteSpace(deliveryInstructions) ? null : deliveryInstructions.Trim();
        IsDefault = isDefault;
        Touch();
    }

    public void SetDefault(bool isDefault)
    {
        IsDefault = isDefault;
        Touch();
    }
}

public class RefreshToken : BaseEntity
{
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public Guid FamilyId { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }

    public bool IsActive => RevokedAt is null && ExpiresAt > DateTimeOffset.UtcNow;

    private RefreshToken() { }

    public RefreshToken(Guid userId, string tokenHash, Guid familyId, DateTimeOffset expiresAt)
    {
        UserId = userId;
        TokenHash = tokenHash;
        FamilyId = familyId;
        ExpiresAt = expiresAt;
    }

    public void Revoke(string? replacedByHash = null)
    {
        RevokedAt ??= DateTimeOffset.UtcNow;
        ReplacedByTokenHash = replacedByHash;
        Touch();
    }
}

public class Cart : BaseEntity
{
    public Guid UserId { get; private set; }
    public string? CouponCode { get; private set; }
    public ICollection<CartItem> Items { get; private set; } = new List<CartItem>();

    private Cart() { }

    public Cart(Guid userId)
    {
        UserId = userId;
    }

    public void AddOrIncrementItem(Guid variantId, int quantity)
    {
        if (quantity < 1 || quantity > 99)
        {
            throw new DomainException("Quantity must be between 1 and 99.", "validation_error", 400);
        }

        var existing = Items.FirstOrDefault(i => i.VariantId == variantId);
        if (existing is not null)
        {
            existing.SetQuantity(Math.Min(99, existing.Quantity + quantity));
        }
        else
        {
            Items.Add(new CartItem(Id, variantId, quantity));
        }
        Touch();
    }

    public void ApplyCoupon(string? code)
    {
        CouponCode = string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
        Touch();
    }

    public void Clear()
    {
        Items.Clear();
        CouponCode = null;
        Touch();
    }
}

public class CartItem : BaseEntity
{
    public Guid CartId { get; private set; }
    public Guid VariantId { get; private set; }
    public int Quantity { get; private set; }

    private CartItem() { }

    public CartItem(Guid cartId, Guid variantId, int quantity)
    {
        CartId = cartId;
        VariantId = variantId;
        SetQuantity(quantity);
    }

    public void SetQuantity(int quantity)
    {
        if (quantity < 1 || quantity > 99)
        {
            throw new DomainException("Quantity must be between 1 and 99.", "validation_error", 400);
        }

        Quantity = quantity;
        Touch();
    }
}

public class Coupon : BaseEntity
{
    public string Code { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DiscountType DiscountType { get; private set; }
    public decimal Value { get; private set; }
    public decimal? MinOrderAmount { get; private set; }
    public decimal? MaxDiscountAmount { get; private set; }
    public DateTimeOffset? StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public int? UsageLimit { get; private set; }
    public int? PerCustomerLimit { get; private set; }
    public int UsedCount { get; private set; }
    public bool IsActive { get; private set; } = true;

    private Coupon() { }

    public Coupon(
        string code,
        string? description,
        DiscountType discountType,
        decimal value,
        decimal? minOrderAmount = null,
        decimal? maxDiscountAmount = null,
        DateTimeOffset? startsAt = null,
        DateTimeOffset? endsAt = null,
        int? usageLimit = null,
        int? perCustomerLimit = null,
        bool isActive = true)
    {
        Update(code, description, discountType, value, minOrderAmount, maxDiscountAmount, startsAt, endsAt, usageLimit, perCustomerLimit, isActive);
    }

    public void Update(
        string code,
        string? description,
        DiscountType discountType,
        decimal value,
        decimal? minOrderAmount,
        decimal? maxDiscountAmount,
        DateTimeOffset? startsAt,
        DateTimeOffset? endsAt,
        int? usageLimit,
        int? perCustomerLimit,
        bool isActive)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new DomainException("Coupon code is required.", "validation_error", 400);
        }
        if (value <= 0m)
        {
            throw new DomainException("Coupon discount value must be positive.", "validation_error", 400);
        }
        if (discountType == DiscountType.Percentage && value > 100m)
        {
            throw new DomainException("Percentage discount cannot exceed 100%.", "validation_error", 400);
        }

        Code = code.Trim().ToUpperInvariant();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        DiscountType = discountType;
        Value = NigerianValueRules.RoundNaira(value);
        MinOrderAmount = minOrderAmount.HasValue ? NigerianValueRules.RoundNaira(minOrderAmount.Value) : null;
        MaxDiscountAmount = maxDiscountAmount.HasValue ? NigerianValueRules.RoundNaira(maxDiscountAmount.Value) : null;
        StartsAt = startsAt;
        EndsAt = endsAt;
        UsageLimit = usageLimit;
        PerCustomerLimit = perCustomerLimit;
        IsActive = isActive;
        Touch();
    }

    public (bool Eligible, decimal Discount, string? Reason) Evaluate(decimal subtotal, int customerUsedCount, DateTimeOffset nowUtc)
    {
        if (!IsActive)
        {
            return (false, 0m, "This coupon is no longer active.");
        }
        if (StartsAt.HasValue && nowUtc < StartsAt.Value)
        {
            return (false, 0m, "This coupon is not yet valid.");
        }
        if (EndsAt.HasValue && nowUtc > EndsAt.Value)
        {
            return (false, 0m, "This coupon has expired.");
        }
        if (UsageLimit.HasValue && UsedCount >= UsageLimit.Value)
        {
            return (false, 0m, "This coupon has reached its maximum redemption limit.");
        }
        if (PerCustomerLimit.HasValue && customerUsedCount >= PerCustomerLimit.Value)
        {
            return (false, 0m, "You have already used this coupon the maximum number of times.");
        }
        if (MinOrderAmount.HasValue && subtotal < MinOrderAmount.Value)
        {
            return (false, 0m, $"Minimum order subtotal of ₦{MinOrderAmount.Value:N2} is required for coupon {Code}.");
        }

        var rawDiscount = DiscountType == DiscountType.Percentage
            ? subtotal * (Value / 100m)
            : Value;

        if (MaxDiscountAmount.HasValue && rawDiscount > MaxDiscountAmount.Value)
        {
            rawDiscount = MaxDiscountAmount.Value;
        }

        var finalDiscount = NigerianValueRules.RoundNaira(Math.Min(subtotal, rawDiscount));
        return (true, finalDiscount, null);
    }

    public void IncrementUsage()
    {
        UsedCount++;
        Touch();
    }
}

public class Promotion : BaseEntity
{
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string? ImageUrl { get; private set; }
    public string? LinkUrl { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public bool IsActive { get; private set; } = true;

    private Promotion() { }

    public Promotion(string title, string description, string? imageUrl, string? linkUrl, DateTimeOffset startsAt, DateTimeOffset? endsAt = null, bool isActive = true)
    {
        Title = title.Trim();
        Description = description.Trim();
        ImageUrl = imageUrl;
        LinkUrl = linkUrl;
        StartsAt = startsAt;
        EndsAt = endsAt;
        IsActive = isActive;
    }
}
