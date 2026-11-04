using System.Security.Cryptography;
using DrinksEcommerce.Application.Common;
using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Application.Contracts;
using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Customers;
using DrinksEcommerce.Domain.Delivery;
using DrinksEcommerce.Domain.Enums;
using DrinksEcommerce.Domain.Inventory;
using DrinksEcommerce.Domain.Orders;
using DrinksEcommerce.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DrinksEcommerce.Application.Checkout;

public sealed class CartAndCheckoutService(
    IApplicationDbContext db,
    IPaymentGateway paymentGateway,
    INotificationService notifications,
    IOptions<StoreOptions> storeOptions,
    IOptions<PaystackOptions> paystackOptions,
    ILogger<CartAndCheckoutService> logger)
{
    public async Task<CartDto> GetCartAsync(Guid userId, CancellationToken ct = default)
    {
        var cart = await GetOrCreateCartEntityAsync(userId, ct);
        return await BuildCartDtoAsync(cart, userId, ct);
    }

    public async Task<CartDto> AddItemAsync(Guid userId, Guid variantId, int quantity, CancellationToken ct = default)
    {
        if (quantity < 1 || quantity > 99)
        {
            throw new DomainException("Quantity must be between 1 and 99.", "validation_error", 400);
        }

        var variant = await db.ProductVariants
            .Include(v => v.Product)
            .Include(v => v.Inventory)
            .FirstOrDefaultAsync(v => v.Id == variantId, ct);

        if (variant is null || !variant.IsActive || variant.Product is null || !variant.Product.IsActive)
        {
            throw DomainException.Unprocessable("This drink pack is currently unavailable.", "product_unavailable");
        }
        if (variant.Product.IsAlcoholic && !storeOptions.Value.AlcoholSalesEnabled)
        {
            throw DomainException.Unprocessable("Alcohol sales are currently disabled by store policy.", "product_unavailable");
        }

        var available = variant.Inventory?.Available ?? 0;
        if (available <= 0)
        {
            throw DomainException.Conflict($"{variant.Product.Name} ({variant.Name}) is currently out of stock.", "insufficient_stock");
        }

        var cart = await GetOrCreateCartEntityAsync(userId, ct);
        var existingQty = cart.Items.FirstOrDefault(i => i.VariantId == variantId)?.Quantity ?? 0;
        if (existingQty + quantity > available)
        {
            throw DomainException.Conflict(
                $"Only {available} unit(s) of {variant.Product.Name} ({variant.Name}) are currently available.",
                "insufficient_stock");
        }

        cart.AddOrIncrementItem(variantId, quantity);
        await db.SaveChangesAsync(ct);
        return await BuildCartDtoAsync(cart, userId, ct);
    }

    public async Task<CartDto> UpdateItemAsync(Guid userId, Guid itemId, int quantity, CancellationToken ct = default)
    {
        var cart = await GetOrCreateCartEntityAsync(userId, ct);
        var item = cart.Items.FirstOrDefault(i => i.Id == itemId)
            ?? throw DomainException.NotFound("Cart item not found.");

        var variant = await db.ProductVariants
            .Include(v => v.Product)
            .Include(v => v.Inventory)
            .FirstOrDefaultAsync(v => v.Id == item.VariantId, ct);

        var available = variant?.Inventory?.Available ?? 0;
        if (quantity > available && available > 0)
        {
            throw DomainException.Conflict($"Only {available} unit(s) are available in stock.", "insufficient_stock");
        }

        item.SetQuantity(quantity);
        cart.Touch();
        await db.SaveChangesAsync(ct);
        return await BuildCartDtoAsync(cart, userId, ct);
    }

    public async Task<CartDto> RemoveItemAsync(Guid userId, Guid itemId, CancellationToken ct = default)
    {
        var cart = await GetOrCreateCartEntityAsync(userId, ct);
        var item = cart.Items.FirstOrDefault(i => i.Id == itemId);
        if (item is not null)
        {
            cart.Items.Remove(item);
            db.CartItems.Remove(item);
            cart.Touch();
            await db.SaveChangesAsync(ct);
        }
        return await BuildCartDtoAsync(cart, userId, ct);
    }

    public async Task ClearCartAsync(Guid userId, CancellationToken ct = default)
    {
        var cart = await GetOrCreateCartEntityAsync(userId, ct);
        foreach (var item in cart.Items.ToList())
        {
            db.CartItems.Remove(item);
        }
        cart.Clear();
        await db.SaveChangesAsync(ct);
    }

    public async Task<CartDto> ApplyCouponAsync(Guid userId, string code, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw DomainException.Unprocessable("Enter a coupon code.", "coupon_invalid");
        }

        var normalized = code.Trim().ToUpperInvariant();
        var coupon = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == normalized, ct)
            ?? throw DomainException.Unprocessable($"Coupon '{normalized}' does not exist.", "coupon_invalid");

        var cart = await GetOrCreateCartEntityAsync(userId, ct);
        var dtoBefore = await BuildCartDtoAsync(cart, userId, ct);

        var customerUsed = await db.Orders.CountAsync(
            o => o.UserId == userId && o.CouponCode == normalized && o.Status != OrderStatus.Cancelled,
            ct);

        var (eligible, _, reason) = coupon.Evaluate(dtoBefore.Subtotal, customerUsed, DateTimeOffset.UtcNow);
        if (!eligible)
        {
            throw DomainException.Unprocessable(reason ?? "Coupon is not valid for this cart.", "coupon_invalid");
        }

        cart.ApplyCoupon(normalized);
        await db.SaveChangesAsync(ct);
        return await BuildCartDtoAsync(cart, userId, ct);
    }

    public async Task<CartDto> RemoveCouponAsync(Guid userId, CancellationToken ct = default)
    {
        var cart = await GetOrCreateCartEntityAsync(userId, ct);
        cart.ApplyCoupon(null);
        await db.SaveChangesAsync(ct);
        return await BuildCartDtoAsync(cart, userId, ct);
    }

    public async Task<CartDto> MergeGuestCartAsync(Guid userId, IReadOnlyList<AddCartItemRequest> items, CancellationToken ct = default)
    {
        var cart = await GetOrCreateCartEntityAsync(userId, ct);
        if (items is not null && items.Count > 0)
        {
            foreach (var req in items.Where(i => i.Quantity > 0 && i.VariantId != Guid.Empty))
            {
                var variant = await db.ProductVariants
                    .Include(v => v.Product)
                    .Include(v => v.Inventory)
                    .FirstOrDefaultAsync(v => v.Id == req.VariantId, ct);

                if (variant is null || !variant.IsActive || variant.Product is null || !variant.Product.IsActive)
                {
                    continue;
                }
                var available = variant.Inventory?.Available ?? 0;
                if (available <= 0)
                {
                    continue;
                }

                var existingQty = cart.Items.FirstOrDefault(i => i.VariantId == req.VariantId)?.Quantity ?? 0;
                var toAdd = Math.Min(req.Quantity, Math.Max(0, available - existingQty));
                if (toAdd > 0)
                {
                    cart.AddOrIncrementItem(req.VariantId, toAdd);
                }
            }

            await db.SaveChangesAsync(ct);
        }

        return await BuildCartDtoAsync(cart, userId, ct);
    }

    public async Task<IReadOnlyList<DeliveryZoneDto>> GetActiveDeliveryZonesAsync(CancellationToken ct = default)
    {
        var zones = await db.DeliveryZones
            .AsNoTracking()
            .Where(z => z.IsActive)
            .OrderBy(z => z.State)
            .ThenBy(z => z.Name)
            .ToListAsync(ct);

        return zones.Select(z => new DeliveryZoneDto(
            z.Id.ToString(),
            z.Name,
            z.State,
            z.GetCities(),
            z.BaseFee,
            z.FreeDeliveryThreshold,
            z.EstimatedMinDays,
            z.EstimatedMaxDays)).ToList();
    }

    public async Task<IReadOnlyList<PickupLocationDto>> GetPickupLocationsAsync(CancellationToken ct = default)
    {
        if (!storeOptions.Value.PickupEnabled)
        {
            return [];
        }

        var locations = await db.PickupLocations
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.State)
            .ThenBy(p => p.Name)
            .ToListAsync(ct);

        return locations.Select(MapPickup).ToList();
    }

    public async Task<DeliveryEstimateDto> EstimateDeliveryAsync(string? state, string? city, decimal subtotal, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(state))
        {
            return new DeliveryEstimateDto(false, null, null, 0m, false, null, null, null, "Select a Nigerian state and city to check delivery coverage.");
        }

        var zones = await db.DeliveryZones
            .AsNoTracking()
            .Include(z => z.FeeRules)
            .Where(z => z.IsActive)
            .ToListAsync(ct);

        // Prefer city-specific zone before wildcard state zone
        var matched = zones
            .Where(z => z.Matches(state, city))
            .OrderByDescending(z => z.GetCities().Count > 0 && !z.GetCities().Contains("*"))
            .FirstOrDefault();

        if (matched is null)
        {
            return new DeliveryEstimateDto(
                false,
                null,
                null,
                0m,
                false,
                null,
                null,
                null,
                $"Delivery is currently unavailable to {city ?? "this area"}, {state}. Please choose a serviceable zone or store pickup.");
        }

        var (fee, freeApplied) = matched.CalculateFee(Math.Max(0m, subtotal));
        var msg = freeApplied
            ? $"Free delivery applied ({matched.Name}, {matched.EstimatedMinDays}–{matched.EstimatedMaxDays} day(s))."
            : $"Standard delivery via {matched.Name} (₦{fee:N2}, est. {matched.EstimatedMinDays}–{matched.EstimatedMaxDays} day(s)).";

        return new DeliveryEstimateDto(
            true,
            matched.Id.ToString(),
            matched.Name,
            fee,
            freeApplied,
            matched.FreeDeliveryThreshold,
            matched.EstimatedMinDays,
            matched.EstimatedMaxDays,
            msg);
    }

    public async Task<CheckoutPreviewDto> PreviewCheckoutAsync(Guid userId, CheckoutRequest request, CancellationToken ct = default)
    {
        var cart = await GetOrCreateCartEntityAsync(userId, ct);
        var cartDto = await BuildCartDtoAsync(cart, userId, ct);
        var warnings = new List<string>(cartDto.Warnings);

        var (serviceable, deliveryFee, minDays, maxDays, message, _) = await ResolveDeliveryCalculationAsync(userId, request, cartDto.Subtotal - cartDto.Discount, false, ct);

        var taxableAmount = Math.Max(0m, cartDto.Subtotal - cartDto.Discount);
        var tax = storeOptions.Value.VatRatePercent > 0m
            ? NigerianValueRules.RoundNaira(taxableAmount * (storeOptions.Value.VatRatePercent / 100m))
            : 0m;

        var total = NigerianValueRules.RoundNaira(taxableAmount + (serviceable ? deliveryFee : 0m) + tax);

        return new CheckoutPreviewDto(
            cartDto.Items,
            cartDto.Subtotal,
            cartDto.Discount,
            serviceable ? deliveryFee : 0m,
            tax,
            total,
            NigerianValueRules.DefaultCurrency,
            cartDto.CouponCode,
            serviceable,
            message,
            minDays,
            maxDays,
            warnings);
    }

    public async Task<(CheckoutResultDto Result, bool CreatedNew)> SubmitCheckoutAsync(
        Guid userId,
        string userFullName,
        string userEmail,
        string? userPhone,
        CheckoutRequest request,
        string idempotencyKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException("Idempotency-Key header is required for checkout.", "validation_error", 400);
        }

        var normalizedKey = idempotencyKey.Trim();
        if (normalizedKey.Length > 100)
        {
            throw new DomainException("Idempotency-Key must not exceed 100 characters.", "validation_error", 400);
        }

        // 1. Check idempotency key for existing order by this user
        var existingOrder = await db.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.UserId == userId && o.IdempotencyKey == normalizedKey, ct);

        if (existingOrder is not null)
        {
            var existingPayment = await db.Payments
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.OrderId == existingOrder.Id, ct);

            return (new CheckoutResultDto(
                existingOrder.Id.ToString(),
                existingOrder.OrderNumber,
                existingOrder.Total,
                existingOrder.Currency,
                existingPayment?.Reference ?? string.Empty,
                existingPayment?.AuthorizationUrl,
                existingPayment?.Status ?? PaymentStatus.Initialized,
                existingOrder.ReservationExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(30),
                "Existing order returned for idempotent retry."), false);
        }

        var contactEmail = string.IsNullOrWhiteSpace(request.ContactEmail) ? userEmail : request.ContactEmail.Trim();

        // 2. Validate delivery or pickup
        var cart = await GetOrCreateCartEntityAsync(userId, ct);
        if (cart.Items.Count == 0)
        {
            throw DomainException.Unprocessable("Your shopping cart is empty.", "cart_empty");
        }

        var variantIds = cart.Items.Select(i => i.VariantId).Distinct().ToList();
        var variants = await db.ProductVariants
            .Include(v => v.Product)
            .Include(v => v.Inventory)
            .Where(v => variantIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, ct);

        var orderItems = new List<OrderItem>();
        var reservationPairs = new List<(Guid VariantId, int Quantity)>();
        decimal subtotal = 0m;

        foreach (var cartItem in cart.Items)
        {
            if (!variants.TryGetValue(cartItem.VariantId, out var variant) ||
                !variant.IsActive ||
                variant.Product is null ||
                !variant.Product.IsActive)
            {
                throw DomainException.Unprocessable("One or more items in your cart are no longer available.", "product_unavailable");
            }

            if (variant.Product.IsAlcoholic && !storeOptions.Value.AlcoholSalesEnabled)
            {
                throw DomainException.Unprocessable("Alcohol products cannot be purchased while alcohol sales are disabled.", "product_unavailable");
            }

            var inv = variant.Inventory
                ?? throw DomainException.Conflict($"Inventory record missing for {variant.Sku}.", "insufficient_stock");

            if (inv.Available < cartItem.Quantity)
            {
                throw DomainException.Conflict(
                    $"Insufficient stock for {variant.Product.Name} ({variant.Name}). Available: {inv.Available}, requested: {cartItem.Quantity}.",
                    "insufficient_stock");
            }

            var primaryImg = await db.ProductImages
                .AsNoTracking()
                .Where(img => img.ProductId == variant.ProductId)
                .OrderBy(img => img.SortOrder)
                .Select(img => img.Url)
                .FirstOrDefaultAsync(ct);

            var orderItem = new OrderItem(
                variant.ProductId,
                variant.Id,
                variant.Product.Name,
                variant.Name,
                variant.Sku,
                primaryImg,
                variant.Price,
                cartItem.Quantity);

            orderItems.Add(orderItem);
            reservationPairs.Add((variant.Id, cartItem.Quantity));
            subtotal += orderItem.LineTotal;
        }

        subtotal = NigerianValueRules.RoundNaira(subtotal);

        // 3. Revalidate coupon on authoritative subtotal
        decimal discount = 0m;
        Coupon? appliedCoupon = null;
        if (!string.IsNullOrWhiteSpace(cart.CouponCode))
        {
            appliedCoupon = await db.Coupons.FirstOrDefaultAsync(c => c.Code == cart.CouponCode, ct);
            if (appliedCoupon is null)
            {
                throw DomainException.Unprocessable($"Coupon '{cart.CouponCode}' is no longer valid.", "coupon_invalid");
            }

            var customerUsed = await db.Orders.CountAsync(
                o => o.UserId == userId && o.CouponCode == appliedCoupon.Code && o.Status != OrderStatus.Cancelled,
                ct);

            var (eligible, evaluatedDiscount, reason) = appliedCoupon.Evaluate(subtotal, customerUsed, DateTimeOffset.UtcNow);
            if (!eligible)
            {
                throw DomainException.Unprocessable(reason ?? "Coupon is not eligible for this order.", "coupon_invalid");
            }
            discount = evaluatedDiscount;
        }

        var (serviceable, deliveryFee, _, _, deliveryMsg, snapshotAddress) = await ResolveDeliveryCalculationAsync(
            userId,
            request,
            Math.Max(0m, subtotal - discount),
            request.SaveAddress == true,
            ct);

        if (!serviceable)
        {
            throw DomainException.Unprocessable(deliveryMsg, "delivery_unavailable");
        }

        var taxableAmount = Math.Max(0m, subtotal - discount);
        var tax = storeOptions.Value.VatRatePercent > 0m
            ? NigerianValueRules.RoundNaira(taxableAmount * (storeOptions.Value.VatRatePercent / 100m))
            : 0m;

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Max(5, storeOptions.Value.ReservationExpiryMinutes));
        var orderNumber = GenerateOrderNumber();
        var paymentRef = GeneratePaymentReference(orderNumber);

        var order = new Order(
            orderNumber,
            userId,
            snapshotAddress?.RecipientName ?? userFullName,
            contactEmail,
            snapshotAddress?.PhoneNumber ?? userPhone,
            request.DeliveryMethod,
            snapshotAddress,
            request.DeliveryMethod == DeliveryMethod.Pickup ? request.PickupLocationId : null,
            request.DeliveryInstructions,
            appliedCoupon?.Code,
            subtotal,
            discount,
            deliveryFee,
            tax,
            normalizedKey,
            expiresAt,
            orderItems);

        var payment = new Payment(order.Id, order.OrderNumber, contactEmail, order.Total, paymentRef);
        var reservation = new StockReservation(order.Id, expiresAt, reservationPairs);

        // 4. Local atomic persistence (do NOT hold SQL transaction open during external HTTP call)
        foreach (var (variantId, qty) in reservationPairs)
        {
            var inv = variants[variantId].Inventory!;
            var tx = inv.Reserve(qty, order.Id, userEmail);
            db.InventoryTransactions.Add(tx);
        }

        appliedCoupon?.IncrementUsage();
        db.Orders.Add(order);
        db.Payments.Add(payment);
        db.StockReservations.Add(reservation);

        foreach (var ci in cart.Items.ToList())
        {
            db.CartItems.Remove(ci);
        }
        cart.Clear();

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            logger.LogWarning(ex, "Inventory concurrency conflict during checkout for user {UserId}", userId);
            throw DomainException.Conflict("Stock levels changed while processing your order. Please review your cart and try again.", "insufficient_stock");
        }
        catch (DbUpdateException ex)
        {
            // Check if a concurrent duplicate idempotency submission won the race
            var concurrentOrder = await db.Orders
                .AsNoTracking()
                .FirstOrDefaultAsync(o => o.UserId == userId && o.IdempotencyKey == normalizedKey, ct);
            if (concurrentOrder is not null)
            {
                var concurrentPayment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == concurrentOrder.Id, ct);
                return (new CheckoutResultDto(
                    concurrentOrder.Id.ToString(),
                    concurrentOrder.OrderNumber,
                    concurrentOrder.Total,
                    concurrentOrder.Currency,
                    concurrentPayment?.Reference ?? string.Empty,
                    concurrentPayment?.AuthorizationUrl,
                    concurrentPayment?.Status ?? PaymentStatus.Initialized,
                    concurrentOrder.ReservationExpiresAt ?? expiresAt,
                    "Existing order returned for idempotent retry."), false);
            }

            logger.LogError(ex, "Database error while creating order for user {UserId}", userId);
            throw;
        }

        // 5. External Paystack initialization AFTER local transaction commit
        var callbackUrl = BuildCallbackUrl(request.Client, payment.Reference);
        var initResult = await paymentGateway.InitializePaymentAsync(
            new PaymentInitializeRequest(
                payment.Reference,
                contactEmail,
                payment.ExpectedAmountKobo,
                payment.Currency,
                callbackUrl,
                order.Id,
                order.OrderNumber),
            ct);

        var attempt = payment.RecordAttempt(
            payment.Reference,
            initResult.AuthorizationUrl,
            initResult.AccessCode,
            initResult.Succeeded ? "Initialized" : (initResult.IsAmbiguousTimeout ? "AmbiguousTimeout" : "Failed"),
            initResult.ErrorMessage);
        db.PaymentAttempts.Add(attempt);

        if (initResult.IsAmbiguousTimeout)
        {
            db.ReconciliationRecords.Add(new ReconciliationRecord(
                ReconciliationType.AmbiguousInitialization,
                payment.Reference,
                order.Id,
                order.OrderNumber,
                order.Total,
                null,
                order.Currency,
                $"Paystack initialization timed out or returned ambiguous state for reference {payment.Reference}."));
        }

        await db.SaveChangesAsync(ct);

        await notifications.QueueAsync(
            userId,
            contactEmail,
            "OrderCreated",
            $"Order {order.OrderNumber} Created",
            $"Your order {order.OrderNumber} for ₦{order.Total:N2} has been created and stock is reserved until {expiresAt:HH:mm} UTC. Complete payment to confirm fulfilment.",
            order.Id,
            ct);

        return (new CheckoutResultDto(
            order.Id.ToString(),
            order.OrderNumber,
            order.Total,
            order.Currency,
            payment.Reference,
            payment.AuthorizationUrl,
            payment.Status,
            expiresAt,
            initResult.Succeeded ? null : (initResult.ErrorMessage ?? "Payment initialization pending. You can retry payment initialization from your order page.")), true);
    }

    public async Task<CheckoutResultDto> InitializePaymentForPendingOrderAsync(
        Guid userId,
        Guid orderId,
        string? client,
        CancellationToken ct = default)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        if (order.Status != OrderStatus.PendingPayment)
        {
            throw DomainException.Conflict($"Order {order.OrderNumber} is in status {order.Status} and cannot initialize a new payment.", "invalid_state_transition");
        }

        var payment = await db.Payments
            .Include(p => p.Attempts)
            .FirstOrDefaultAsync(p => p.OrderId == order.Id, ct)
            ?? throw DomainException.NotFound("Payment record not found for order.");

        if (payment.Status == PaymentStatus.Successful)
        {
            throw DomainException.Conflict("Order has already been paid.", "invalid_state_transition");
        }

        // Reuse authorization URL if already pending and not failed
        if (payment.Status == PaymentStatus.Pending && !string.IsNullOrWhiteSpace(payment.AuthorizationUrl))
        {
            return new CheckoutResultDto(
                order.Id.ToString(),
                order.OrderNumber,
                order.Total,
                order.Currency,
                payment.Reference,
                payment.AuthorizationUrl,
                payment.Status,
                order.ReservationExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(15),
                null);
        }

        // Generate a fresh reference if previous attempt failed or had no URL
        var newRef = payment.Attempts.Count > 0 ? GeneratePaymentReference(order.OrderNumber) : payment.Reference;
        var callbackUrl = BuildCallbackUrl(client, newRef);

        var initResult = await paymentGateway.InitializePaymentAsync(
            new PaymentInitializeRequest(
                newRef,
                order.ContactEmail,
                payment.ExpectedAmountKobo,
                payment.Currency,
                callbackUrl,
                order.Id,
                order.OrderNumber),
            ct);

        var attempt = payment.RecordAttempt(
            newRef,
            initResult.AuthorizationUrl,
            initResult.AccessCode,
            initResult.Succeeded ? "Initialized" : "Failed",
            initResult.ErrorMessage);
        db.PaymentAttempts.Add(attempt);
        await db.SaveChangesAsync(ct);

        return new CheckoutResultDto(
            order.Id.ToString(),
            order.OrderNumber,
            order.Total,
            order.Currency,
            payment.Reference,
            payment.AuthorizationUrl,
            payment.Status,
            order.ReservationExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(15),
            initResult.ErrorMessage);
    }

    private string BuildCallbackUrl(string? client, string reference)
    {
        var baseCallback = string.Equals(client, "mobile", StringComparison.OrdinalIgnoreCase)
            ? paystackOptions.Value.MobileCallbackUrl
            : paystackOptions.Value.WebCallbackUrl;

        var sep = baseCallback.Contains('?') ? "&" : "?";
        return $"{baseCallback}{sep}reference={Uri.EscapeDataString(reference)}";
    }

    private async Task<(bool Serviceable, decimal Fee, int? MinDays, int? MaxDays, string Message, OrderAddressSnapshot? Snapshot)> ResolveDeliveryCalculationAsync(
        Guid userId,
        CheckoutRequest request,
        decimal netSubtotal,
        bool persistNewAddress,
        CancellationToken ct)
    {
        if (request.DeliveryMethod == DeliveryMethod.Pickup)
        {
            if (!storeOptions.Value.PickupEnabled)
            {
                return (false, 0m, null, null, "Store pickup is not currently enabled.", null);
            }
            if (!request.PickupLocationId.HasValue)
            {
                return (false, 0m, null, null, "Please select a pickup location.", null);
            }
            var loc = await db.PickupLocations.AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PickupLocationId.Value && p.IsActive, ct);
            if (loc is null)
            {
                return (false, 0m, null, null, "Selected pickup location is unavailable.", null);
            }

            return (true, 0m, 0, 1, $"Pickup at {loc.Name} ({loc.OpeningHours}).", null);
        }

        OrderAddressSnapshot snapshot;
        if (request.AddressId.HasValue && request.AddressId.Value != Guid.Empty)
        {
            var saved = await db.CustomerAddresses.AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AddressId.Value && a.UserId == userId, ct)
                ?? throw DomainException.NotFound("Selected delivery address was not found.");

            snapshot = new OrderAddressSnapshot(saved.RecipientName, saved.PhoneNumber, saved.State, saved.City, saved.Street, saved.Landmark, saved.PostalCode);
        }
        else if (request.Address is not null)
        {
            var addr = request.Address;
            var normalizedPhone = NigerianValueRules.NormalizeNigerianPhoneOrThrow(addr.PhoneNumber);
            if (string.IsNullOrWhiteSpace(addr.RecipientName) ||
                string.IsNullOrWhiteSpace(addr.State) ||
                string.IsNullOrWhiteSpace(addr.City) ||
                string.IsNullOrWhiteSpace(addr.Street))
            {
                throw new DomainException("Recipient name, state, city/LGA, and street address are required.", "validation_error", 400);
            }

            snapshot = new OrderAddressSnapshot(
                addr.RecipientName.Trim(),
                normalizedPhone,
                addr.State.Trim(),
                addr.City.Trim(),
                addr.Street.Trim(),
                string.IsNullOrWhiteSpace(addr.Landmark) ? null : addr.Landmark.Trim(),
                string.IsNullOrWhiteSpace(addr.PostalCode) ? null : addr.PostalCode.Trim());

            if (persistNewAddress)
            {
                var entity = new CustomerAddress(
                    userId,
                    addr.Label,
                    addr.RecipientName,
                    normalizedPhone,
                    addr.State,
                    addr.City,
                    addr.Street,
                    addr.Landmark,
                    addr.PostalCode,
                    addr.DeliveryInstructions,
                    addr.IsDefault == true);
                db.CustomerAddresses.Add(entity);
            }
        }
        else
        {
            return (false, 0m, null, null, "Please provide a delivery address.", null);
        }

        var estimate = await EstimateDeliveryAsync(snapshot.State, snapshot.City, netSubtotal, ct);
        return (estimate.Serviceable, estimate.Fee, estimate.EstimatedMinDays, estimate.EstimatedMaxDays, estimate.Message, snapshot);
    }

    private async Task<Cart> GetOrCreateCartEntityAsync(Guid userId, CancellationToken ct)
    {
        var cart = await db.Carts
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.UserId == userId, ct);

        if (cart is not null)
        {
            return cart;
        }

        cart = new Cart(userId);
        db.Carts.Add(cart);
        await db.SaveChangesAsync(ct);
        return cart;
    }

    private async Task<CartDto> BuildCartDtoAsync(Cart cart, Guid userId, CancellationToken ct)
    {
        var warnings = new List<string>();
        var variantIds = cart.Items.Select(i => i.VariantId).Distinct().ToList();
        var variants = await db.ProductVariants
            .AsNoTracking()
            .Include(v => v.Product)
                .ThenInclude(p => p!.Images)
            .Include(v => v.Inventory)
            .Where(v => variantIds.Contains(v.Id))
            .ToDictionaryAsync(v => v.Id, ct);

        var itemsDto = new List<CartItemDto>();
        decimal subtotal = 0m;
        var dirty = false;

        foreach (var item in cart.Items.ToList())
        {
            if (!variants.TryGetValue(item.VariantId, out var v) || v.Product is null || !v.IsActive || !v.Product.IsActive)
            {
                warnings.Add("An unavailable product pack was removed from your cart.");
                cart.Items.Remove(item);
                db.CartItems.Remove(item);
                dirty = true;
                continue;
            }

            var available = v.Inventory?.Available ?? 0;
            var status = v.Inventory?.GetStockStatus() ?? StockStatus.OutOfStock;
            var isAvailable = available > 0 && v.IsActive && v.Product.IsActive;

            if (available > 0 && item.Quantity > available)
            {
                warnings.Add($"{v.Product.Name} ({v.Name}): only {available} left in stock, quantity adjusted.");
                item.SetQuantity(available);
                dirty = true;
            }
            else if (available == 0)
            {
                warnings.Add($"{v.Product.Name} ({v.Name}) is currently out of stock.");
            }

            var lineTotal = NigerianValueRules.RoundNaira(v.Price * item.Quantity);
            if (isAvailable)
            {
                subtotal += lineTotal;
            }

            var img = v.Product.Images.OrderBy(i => i.SortOrder).FirstOrDefault()?.Url;
            itemsDto.Add(new CartItemDto(
                item.Id.ToString(),
                v.ProductId.ToString(),
                v.Product.Name,
                v.Product.Slug,
                v.Id.ToString(),
                v.Name,
                v.Sku,
                img,
                v.Price,
                item.Quantity,
                lineTotal,
                Math.Min(99, available),
                status,
                isAvailable));
        }

        subtotal = NigerianValueRules.RoundNaira(subtotal);
        decimal discount = 0m;
        var couponCode = cart.CouponCode;

        if (!string.IsNullOrWhiteSpace(couponCode))
        {
            var coupon = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == couponCode, ct);
            var customerUsed = await db.Orders.CountAsync(
                o => o.UserId == userId && o.CouponCode == couponCode && o.Status != OrderStatus.Cancelled,
                ct);

            if (coupon is null)
            {
                warnings.Add($"Coupon '{couponCode}' was removed because it no longer exists.");
                cart.ApplyCoupon(null);
                couponCode = null;
                dirty = true;
            }
            else
            {
                var (eligible, disc, reason) = coupon.Evaluate(subtotal, customerUsed, DateTimeOffset.UtcNow);
                if (!eligible)
                {
                    warnings.Add(reason ?? $"Coupon '{couponCode}' is not currently applicable.");
                }
                else
                {
                    discount = disc;
                }
            }
        }

        if (dirty)
        {
            await db.SaveChangesAsync(ct);
        }

        var total = NigerianValueRules.RoundNaira(Math.Max(0m, subtotal - discount));
        return new CartDto(
            cart.Id.ToString(),
            itemsDto,
            itemsDto.Sum(i => i.Quantity),
            subtotal,
            discount,
            couponCode,
            total,
            warnings);
    }

    public static OrderPickupHelper MapPickupHelper => new();
    private static PickupLocationDto MapPickup(PickupLocation p) =>
        new(p.Id.ToString(), p.Name, p.Address, p.State, p.City, p.OpeningHours, p.PhoneNumber);

    private static string GenerateOrderNumber()
    {
        var datePart = DateTimeOffset.UtcNow.ToString("yyyyMMdd");
        var randomBytes = RandomNumberGenerator.GetBytes(3);
        var hex = Convert.ToHexString(randomBytes);
        return $"DRK-{datePart}-{hex}";
    }

    public static string GeneratePaymentReference(string orderNumber)
    {
        var randomBytes = RandomNumberGenerator.GetBytes(6);
        var hex = Convert.ToHexString(randomBytes).ToLowerInvariant();
        return $"pay_{orderNumber.ToLowerInvariant().Replace('-', '_')}_{hex}";
    }

    public sealed class OrderPickupHelper;
}
