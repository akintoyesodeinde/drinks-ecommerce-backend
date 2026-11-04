using DrinksEcommerce.Application.Common;
using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Application.Contracts;
using DrinksEcommerce.Application.Payments;
using DrinksEcommerce.Application.Products;
using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Customers;
using DrinksEcommerce.Domain.Delivery;
using DrinksEcommerce.Domain.Enums;
using DrinksEcommerce.Domain.Inventory;
using DrinksEcommerce.Domain.Orders;
using DrinksEcommerce.Domain.Payments;
using DrinksEcommerce.Domain.Products;
using Microsoft.EntityFrameworkCore;

namespace DrinksEcommerce.Application.Orders;

public sealed class OrderAndAdminService(
    IApplicationDbContext db,
    PaymentOrchestratorService paymentOrchestrator,
    ICatalogCacheService catalogCache,
    INotificationService notifications,
    IAuditService audit)
{
    // Customer Orders & Tracking
    public async Task<PagedResult<OrderSummaryDto>> GetMyOrdersAsync(Guid userId, OrderStatus? status, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.UserId == userId);

        if (status.HasValue)
        {
            query = query.Where(o => o.Status == status.Value);
        }

        var total = await query.CountAsync(ct);
        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var orderIds = orders.Select(o => o.Id).ToList();
        var payments = await db.Payments
            .AsNoTracking()
            .Where(p => orderIds.Contains(p.OrderId))
            .ToDictionaryAsync(p => p.OrderId, ct);

        var items = orders.Select(o => new OrderSummaryDto(
            o.Id.ToString(),
            o.OrderNumber,
            o.Status,
            payments.TryGetValue(o.Id, out var p) ? p.Status : null,
            o.DeliveryMethod,
            o.Total,
            o.Currency,
            o.Items.Sum(i => i.Quantity),
            o.CreatedAt)).ToList();

        return PagedResult<OrderSummaryDto>.Create(items, page, pageSize, total);
    }

    public async Task<OrderDetailDto> GetMyOrderDetailAsync(Guid userId, Guid orderId, CancellationToken ct = default)
    {
        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        return await BuildOrderDetailDtoAsync(order, ct);
    }

    public async Task<OrderDetailDto> CancelMyOrderAsync(Guid userId, string userEmail, Guid orderId, string? reason, CancellationToken ct = default)
    {
        var order = await db.Orders
            .Include(o => o.Items)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        if (!order.CanCustomerCancel)
        {
            throw DomainException.Conflict($"Order {order.OrderNumber} can no longer be cancelled because its status is {order.Status}.", "invalid_state_transition");
        }

        var note = string.IsNullOrWhiteSpace(reason) ? "Cancelled by customer." : $"Customer cancellation: {reason.Trim()}";
        if (order.Status == OrderStatus.PendingPayment)
        {
            var hist = order.TransitionTo(OrderStatus.Cancelled, note, userEmail);
            db.OrderStatusHistories.Add(hist);
            await paymentOrchestrator.ReleaseOrderReservationIfActiveAsync(order, note, userEmail, ct);

            var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == order.Id, ct);
            payment?.MarkFailedOrAbandoned(abandoned: true, "Order cancelled by customer.", userEmail);
        }
        else
        {
            var hist = order.TransitionTo(OrderStatus.CancellationRequested, note, userEmail);
            db.OrderStatusHistories.Add(hist);
        }

        await db.SaveChangesAsync(ct);

        await notifications.QueueAsync(
            order.UserId,
            order.ContactEmail,
            "OrderCancellation",
            $"Order {order.OrderNumber} Update",
            order.Status == OrderStatus.Cancelled
                ? $"Your order {order.OrderNumber} has been cancelled."
                : $"Your cancellation request for order {order.OrderNumber} has been submitted for staff review.",
            order.Id,
            ct);

        return await BuildOrderDetailDtoAsync(order, ct);
    }

    public async Task<TrackingDto> GetOrderTrackingAsync(Guid callerUserId, bool isStaff, Guid orderId, CancellationToken ct = default)
    {
        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        if (!isStaff && order.UserId != callerUserId)
        {
            throw DomainException.NotFound("Order not found.");
        }

        var delivery = await db.OrderDeliveries
            .AsNoTracking()
            .Include(d => d.History)
            .FirstOrDefaultAsync(d => d.OrderId == order.Id, ct);

        var stepDefinitions = order.DeliveryMethod == DeliveryMethod.Pickup
            ? new (string Key, string Label, OrderStatus[] ReachedStatuses)[]
            {
                ("placed", "Order Placed", [OrderStatus.PendingPayment, OrderStatus.Confirmed, OrderStatus.Processing, OrderStatus.ReadyForDispatch, OrderStatus.Delivered]),
                ("paid", "Payment Verified", [OrderStatus.Confirmed, OrderStatus.Processing, OrderStatus.ReadyForDispatch, OrderStatus.Delivered]),
                ("processing", "Preparing Order", [OrderStatus.Processing, OrderStatus.ReadyForDispatch, OrderStatus.Delivered]),
                ("ready", "Ready for Pickup", [OrderStatus.ReadyForDispatch, OrderStatus.Delivered]),
                ("delivered", "Picked Up", [OrderStatus.Delivered])
            }
            : new (string Key, string Label, OrderStatus[] ReachedStatuses)[]
            {
                ("placed", "Order Placed", [OrderStatus.PendingPayment, OrderStatus.Confirmed, OrderStatus.Processing, OrderStatus.ReadyForDispatch, OrderStatus.OutForDelivery, OrderStatus.Delivered]),
                ("paid", "Payment Verified", [OrderStatus.Confirmed, OrderStatus.Processing, OrderStatus.ReadyForDispatch, OrderStatus.OutForDelivery, OrderStatus.Delivered]),
                ("processing", "Preparing Order", [OrderStatus.Processing, OrderStatus.ReadyForDispatch, OrderStatus.OutForDelivery, OrderStatus.Delivered]),
                ("ready", "Ready for Dispatch", [OrderStatus.ReadyForDispatch, OrderStatus.OutForDelivery, OrderStatus.Delivered]),
                ("out_for_delivery", "Out for Delivery", [OrderStatus.OutForDelivery, OrderStatus.Delivered]),
                ("delivered", "Delivered", [OrderStatus.Delivered])
            };

        var steps = new List<TrackingStepDto>();
        for (var i = 0; i < stepDefinitions.Length; i++)
        {
            var def = stepDefinitions[i];
            var completed = def.ReachedStatuses.Contains(order.Status);
            var isLastReached = completed && (i == stepDefinitions.Length - 1 || !stepDefinitions[i + 1].ReachedStatuses.Contains(order.Status));

            DateTimeOffset? occurredAt = def.Key switch
            {
                "placed" => order.CreatedAt,
                "paid" => order.ConfirmedAt,
                "out_for_delivery" => delivery?.DispatchedAt,
                "delivered" => order.DeliveredAt ?? delivery?.DeliveredAt,
                _ => order.StatusHistory
                    .Where(h => string.Equals(h.Status, def.ReachedStatuses.First().ToString(), StringComparison.OrdinalIgnoreCase))
                    .Select(h => (DateTimeOffset?)h.CreatedAt)
                    .FirstOrDefault()
            };

            steps.Add(new TrackingStepDto(def.Key, def.Label, completed, isLastReached, occurredAt));
        }

        var combinedHistory = order.StatusHistory
            .Select(h => new StatusHistoryDto(h.Status, h.Note, h.Actor, h.CreatedAt))
            .Concat(delivery?.History.Select(h => new StatusHistoryDto($"Delivery:{h.Status}", h.Note, h.Actor, h.CreatedAt)) ?? [])
            .OrderByDescending(h => h.CreatedAt)
            .ToList();

        var rider = delivery is not null && !string.IsNullOrWhiteSpace(delivery.RiderName)
            ? new RiderInfoDto(delivery.RiderName, delivery.RiderPhone ?? string.Empty)
            : null;

        var estFrom = order.ConfirmedAt?.AddDays(1);
        var estTo = order.ConfirmedAt?.AddDays(2);

        return new TrackingDto(
            order.Id.ToString(),
            order.OrderNumber,
            order.Status,
            delivery?.Status,
            order.DeliveryMethod,
            estFrom,
            estTo,
            rider,
            steps,
            combinedHistory);
    }

    // Admin Products & Categories
    public async Task<PagedResult<AdminProductSummaryDto>> GetAdminProductsAsync(
        string? search,
        Guid? categoryId,
        bool includeInactive,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Images)
            .Include(p => p.Variants)
                .ThenInclude(v => v.Inventory)
            .AsQueryable();

        if (!includeInactive)
        {
            query = query.Where(p => p.IsActive);
        }
        if (categoryId.HasValue && categoryId.Value != Guid.Empty)
        {
            query = query.Where(p => p.CategoryId == categoryId.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(p =>
                p.Name.ToLower().Contains(term) ||
                p.Brand.ToLower().Contains(term) ||
                p.Variants.Any(v => v.Sku.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(ct);
        var products = await query
            .OrderByDescending(p => p.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var items = products.Select(p =>
        {
            var prices = p.Variants.Select(v => v.Price).ToList();
            return new AdminProductSummaryDto(
                p.Id.ToString(),
                p.Slug,
                p.Name,
                p.Brand,
                p.Category?.Name ?? string.Empty,
                p.Images.OrderBy(i => i.SortOrder).FirstOrDefault()?.Url,
                p.IsActive,
                p.IsAlcoholic,
                p.IsFeatured,
                p.Variants.Count,
                prices.Count > 0 ? prices.Min() : 0m,
                prices.Count > 0 ? prices.Max() : 0m,
                p.Variants.Sum(v => v.Inventory?.Available ?? 0),
                p.UpdatedAt);
        }).ToList();

        return PagedResult<AdminProductSummaryDto>.Create(items, page, pageSize, total);
    }

    public async Task<AdminProductDto> GetAdminProductByIdAsync(Guid id, CancellationToken ct = default)
    {
        var p = await db.Products
            .AsNoTracking()
            .Include(x => x.Category)
            .Include(x => x.Images)
            .Include(x => x.Attributes)
            .Include(x => x.Variants)
                .ThenInclude(v => v.Inventory)
            .FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw DomainException.NotFound("Product not found.");

        return MapAdminProduct(p);
    }

    public async Task<AdminProductDto> CreateProductAsync(CreateProductRequest request, string? actor, CancellationToken ct = default)
    {
        if (request.Variants is null || request.Variants.Count == 0)
        {
            throw new DomainException("At least one product variant/pack is required.", "validation_error", 400);
        }

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == request.CategoryId, ct)
            ?? throw DomainException.NotFound("Category not found.");

        var product = new Product(
            request.Name,
            request.Description,
            request.Brand,
            category.Id,
            request.IsAlcoholic,
            request.IsFeatured,
            request.IsActive);

        // Ensure unique slug
        if (await db.Products.AnyAsync(p => p.Slug == product.Slug, ct))
        {
            product.SetSlug($"{product.Slug}-{Guid.NewGuid().ToString("N")[..4]}");
        }

        if (request.Attributes is not null)
        {
            foreach (var attr in request.Attributes.Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Value)))
            {
                product.Attributes.Add(new ProductAttribute(product.Id, attr.Name, attr.Value));
            }
        }

        foreach (var vReq in request.Variants)
        {
            var normSku = vReq.Sku.Trim().ToUpperInvariant();
            if (await db.ProductVariants.AnyAsync(v => v.Sku == normSku, ct))
            {
                throw DomainException.Conflict($"SKU '{normSku}' already exists.", "duplicate_sku");
            }

            var variant = new ProductVariant(
                product.Id,
                normSku,
                vReq.Name,
                vReq.PackType,
                vReq.UnitsPerPack,
                vReq.UnitVolumeMl,
                vReq.Price,
                vReq.CompareAtPrice,
                vReq.IsActive);

            var initialStock = Math.Max(0, vReq.InitialStock ?? 0);
            var inv = new InventoryItem(variant.Id, initialStock, Math.Max(0, vReq.LowStockThreshold));
            db.InventoryItems.Add(inv);
            if (initialStock > 0)
            {
                db.InventoryTransactions.Add(new InventoryTransaction(
                    variant.Id,
                    InventoryTransactionType.InitialStock,
                    initialStock,
                    initialStock,
                    0,
                    "Initial stock on product creation.",
                    null,
                    actor));
            }

            product.Variants.Add(variant);
        }

        db.Products.Add(product);
        audit.Record("CreateProduct", "Product", product.Id.ToString(), $"Created product '{product.Name}' with {product.Variants.Count} variant(s).");
        await db.SaveChangesAsync(ct);
        await catalogCache.InvalidateCatalogAsync(ct);

        return await GetAdminProductByIdAsync(product.Id, ct);
    }

    public async Task<AdminProductDto> UpdateProductAsync(Guid id, UpdateProductRequest request, string? actor, CancellationToken ct = default)
    {
        var product = await db.Products
            .Include(p => p.Attributes)
            .FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw DomainException.NotFound("Product not found.");

        if (!await db.Categories.AnyAsync(c => c.Id == request.CategoryId, ct))
        {
            throw DomainException.NotFound("Category not found.");
        }

        product.UpdateDetails(
            request.Name,
            request.Description,
            request.Brand,
            request.CategoryId,
            request.IsAlcoholic,
            request.IsFeatured,
            request.IsActive);

        foreach (var existing in product.Attributes.ToList())
        {
            db.ProductAttributes.Remove(existing);
        }
        product.Attributes.Clear();

        if (request.Attributes is not null)
        {
            foreach (var attr in request.Attributes.Where(a => !string.IsNullOrWhiteSpace(a.Name) && !string.IsNullOrWhiteSpace(a.Value)))
            {
                var entity = new ProductAttribute(product.Id, attr.Name, attr.Value);
                product.Attributes.Add(entity);
                db.ProductAttributes.Add(entity);
            }
        }

        audit.Record("UpdateProduct", "Product", product.Id.ToString(), $"Updated product '{product.Name}'.");
        await db.SaveChangesAsync(ct);
        await catalogCache.InvalidateCatalogAsync(ct);
        return await GetAdminProductByIdAsync(product.Id, ct);
    }

    public async Task DeactivateProductAsync(Guid id, string? actor, CancellationToken ct = default)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw DomainException.NotFound("Product not found.");

        product.Deactivate();
        audit.Record("DeactivateProduct", "Product", product.Id.ToString(), $"Deactivated product '{product.Name}'.");
        await db.SaveChangesAsync(ct);
        await catalogCache.InvalidateCatalogAsync(ct);
    }

    public async Task<AdminProductDto> AddVariantAsync(Guid productId, VariantInput input, string? actor, CancellationToken ct = default)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, ct)
            ?? throw DomainException.NotFound("Product not found.");

        var normSku = input.Sku.Trim().ToUpperInvariant();
        if (await db.ProductVariants.AnyAsync(v => v.Sku == normSku, ct))
        {
            throw DomainException.Conflict($"SKU '{normSku}' already exists.", "duplicate_sku");
        }

        var variant = new ProductVariant(
            product.Id,
            normSku,
            input.Name,
            input.PackType,
            input.UnitsPerPack,
            input.UnitVolumeMl,
            input.Price,
            input.CompareAtPrice,
            input.IsActive);

        var initialStock = Math.Max(0, input.InitialStock ?? 0);
        var inv = new InventoryItem(variant.Id, initialStock, Math.Max(0, input.LowStockThreshold));

        db.ProductVariants.Add(variant);
        db.InventoryItems.Add(inv);
        if (initialStock > 0)
        {
            db.InventoryTransactions.Add(new InventoryTransaction(
                variant.Id,
                InventoryTransactionType.InitialStock,
                initialStock,
                initialStock,
                0,
                "Initial stock on variant creation.",
                null,
                actor));
        }

        audit.Record("AddVariant", "ProductVariant", variant.Id.ToString(), $"Added variant {variant.Sku} ({variant.Name}) to product {product.Name}.");
        await db.SaveChangesAsync(ct);
        await catalogCache.InvalidateCatalogAsync(ct);
        return await GetAdminProductByIdAsync(productId, ct);
    }

    public async Task<AdminProductDto> UpdateVariantAsync(Guid productId, Guid variantId, VariantInput input, string? actor, CancellationToken ct = default)
    {
        var variant = await db.ProductVariants
            .Include(v => v.Inventory)
            .FirstOrDefaultAsync(v => v.Id == variantId && v.ProductId == productId, ct)
            ?? throw DomainException.NotFound("Product variant not found.");

        var normSku = input.Sku.Trim().ToUpperInvariant();
        if (await db.ProductVariants.AnyAsync(v => v.Sku == normSku && v.Id != variantId, ct))
        {
            throw DomainException.Conflict($"SKU '{normSku}' is already used by another variant.", "duplicate_sku");
        }

        variant.Update(normSku, input.Name, input.PackType, input.UnitsPerPack, input.UnitVolumeMl, input.Price, input.CompareAtPrice, input.IsActive);
        variant.Inventory?.UpdateThreshold(Math.Max(0, input.LowStockThreshold));

        audit.Record("UpdateVariant", "ProductVariant", variant.Id.ToString(), $"Updated variant {variant.Sku} price to ₦{variant.Price:N2}.");
        await db.SaveChangesAsync(ct);
        await catalogCache.InvalidateCatalogAsync(ct);
        return await GetAdminProductByIdAsync(productId, ct);
    }

    public async Task<AdminProductDto> AddProductImageAsync(Guid productId, string url, string? altText, string? actor, CancellationToken ct = default)
    {
        var product = await db.Products.Include(p => p.Images).FirstOrDefaultAsync(p => p.Id == productId, ct)
            ?? throw DomainException.NotFound("Product not found.");

        var nextOrder = product.Images.Count == 0 ? 0 : product.Images.Max(i => i.SortOrder) + 1;
        var img = new ProductImage(productId, url, altText, nextOrder);
        db.ProductImages.Add(img);
        audit.Record("AddProductImage", "ProductImage", img.Id.ToString(), $"Uploaded image {url} for product {product.Name}.");
        await db.SaveChangesAsync(ct);
        await catalogCache.InvalidateCatalogAsync(ct);
        return await GetAdminProductByIdAsync(productId, ct);
    }

    public async Task<AdminProductDto> DeleteProductImageAsync(Guid productId, Guid imageId, string? actor, CancellationToken ct = default)
    {
        var img = await db.ProductImages.FirstOrDefaultAsync(i => i.Id == imageId && i.ProductId == productId, ct)
            ?? throw DomainException.NotFound("Product image not found.");

        db.ProductImages.Remove(img);
        audit.Record("DeleteProductImage", "ProductImage", imageId.ToString(), $"Deleted image {img.Url}.");
        await db.SaveChangesAsync(ct);
        await catalogCache.InvalidateCatalogAsync(ct);
        return await GetAdminProductByIdAsync(productId, ct);
    }

    // Categories Admin
    public async Task<IReadOnlyList<AdminCategoryDto>> GetAdminCategoriesAsync(CancellationToken ct = default)
    {
        var categories = await db.Categories
            .AsNoTracking()
            .Include(c => c.Products)
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);

        return categories.Select(c => new AdminCategoryDto(
            c.Id.ToString(),
            c.Name,
            c.Slug,
            c.Description,
            c.ImageUrl,
            c.Products.Count,
            c.IsActive,
            c.SortOrder)).ToList();
    }

    public async Task<AdminCategoryDto> CreateCategoryAsync(CategoryInput input, string? actor, CancellationToken ct = default)
    {
        var category = new Category(input.Name, input.Description, input.ImageUrl, input.SortOrder, input.IsActive);
        if (await db.Categories.AnyAsync(c => c.Slug == category.Slug, ct))
        {
            throw DomainException.Conflict($"Category '{input.Name}' already exists.");
        }

        db.Categories.Add(category);
        audit.Record("CreateCategory", "Category", category.Id.ToString(), $"Created category '{category.Name}'.");
        await db.SaveChangesAsync(ct);
        await catalogCache.InvalidateCatalogAsync(ct);

        return new AdminCategoryDto(category.Id.ToString(), category.Name, category.Slug, category.Description, category.ImageUrl, 0, category.IsActive, category.SortOrder);
    }

    public async Task<AdminCategoryDto> UpdateCategoryAsync(Guid id, CategoryInput input, string? actor, CancellationToken ct = default)
    {
        var category = await db.Categories.Include(c => c.Products).FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw DomainException.NotFound("Category not found.");

        category.Update(input.Name, input.Description, input.ImageUrl ?? category.ImageUrl, input.SortOrder, input.IsActive);
        audit.Record("UpdateCategory", "Category", category.Id.ToString(), $"Updated category '{category.Name}'.");
        await db.SaveChangesAsync(ct);
        await catalogCache.InvalidateCatalogAsync(ct);

        return new AdminCategoryDto(category.Id.ToString(), category.Name, category.Slug, category.Description, category.ImageUrl, category.Products.Count, category.IsActive, category.SortOrder);
    }

    public async Task DeleteCategoryAsync(Guid id, string? actor, CancellationToken ct = default)
    {
        var category = await db.Categories.Include(c => c.Products).FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw DomainException.NotFound("Category not found.");

        if (category.Products.Count > 0)
        {
            throw DomainException.Conflict("Cannot delete a category that still contains products.");
        }

        db.Categories.Remove(category);
        audit.Record("DeleteCategory", "Category", id.ToString(), $"Deleted category '{category.Name}'.");
        await db.SaveChangesAsync(ct);
        await catalogCache.InvalidateCatalogAsync(ct);
    }

    // Inventory Admin
    public async Task<PagedResult<InventoryItemDto>> GetInventoryAsync(string? search, bool lowStockOnly, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.InventoryItems
            .AsNoTracking()
            .Include(i => i.Variant)
                .ThenInclude(v => v!.Product)
            .Where(i => i.Variant != null && i.Variant.Product != null);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(i =>
                i.Variant!.Sku.ToLower().Contains(term) ||
                i.Variant.Name.ToLower().Contains(term) ||
                i.Variant.Product!.Name.ToLower().Contains(term));
        }

        if (lowStockOnly)
        {
            query = query.Where(i => (i.OnHand - i.Reserved) <= i.LowStockThreshold);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(i => i.OnHand - i.Reserved)
            .ThenBy(i => i.Variant!.Sku)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var dtos = items.Select(MapInventoryItem).ToList();
        return PagedResult<InventoryItemDto>.Create(dtos, page, pageSize, total);
    }

    public async Task<InventoryItemDto> AdjustInventoryAsync(Guid variantId, AdjustInventoryRequest request, string? actor, CancellationToken ct = default)
    {
        var inv = await db.InventoryItems
            .Include(i => i.Variant)
                .ThenInclude(v => v!.Product)
            .FirstOrDefaultAsync(i => i.VariantId == variantId, ct)
            ?? throw DomainException.NotFound("Inventory item not found.");

        var tx = inv.Adjust(request.QuantityChange, request.Type, request.Reason, actor);
        db.InventoryTransactions.Add(tx);
        audit.Record("AdjustInventory", "InventoryItem", variantId.ToString(), $"Adjusted SKU {inv.Variant?.Sku} by {request.QuantityChange} ({request.Type}): {request.Reason}");
        await db.SaveChangesAsync(ct);
        await catalogCache.InvalidateCatalogAsync(ct);
        return MapInventoryItem(inv);
    }

    public async Task<InventoryItemDto> UpdateThresholdAsync(Guid variantId, int threshold, string? actor, CancellationToken ct = default)
    {
        var inv = await db.InventoryItems
            .Include(i => i.Variant)
                .ThenInclude(v => v!.Product)
            .FirstOrDefaultAsync(i => i.VariantId == variantId, ct)
            ?? throw DomainException.NotFound("Inventory item not found.");

        inv.UpdateThreshold(threshold);
        audit.Record("UpdateLowStockThreshold", "InventoryItem", variantId.ToString(), $"Set low-stock threshold for {inv.Variant?.Sku} to {threshold}.");
        await db.SaveChangesAsync(ct);
        return MapInventoryItem(inv);
    }

    public async Task<PagedResult<InventoryTransactionDto>> GetInventoryTransactionsAsync(Guid variantId, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.InventoryTransactions.AsNoTracking().Where(t => t.VariantId == variantId);
        var total = await query.CountAsync(ct);
        var list = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var dtos = list.Select(t => new InventoryTransactionDto(
            t.Id.ToString(),
            t.Type,
            t.QuantityChange,
            t.OnHandAfter,
            t.ReservedAfter,
            t.Reason,
            t.OrderId?.ToString(),
            t.Actor,
            t.CreatedAt)).ToList();

        return PagedResult<InventoryTransactionDto>.Create(dtos, page, pageSize, total);
    }

    // Admin Orders & Fulfilment
    public async Task<PagedResult<AdminOrderSummaryDto>> GetAdminOrdersAsync(
        OrderStatus? status,
        string? search,
        DateTimeOffset? from,
        DateTimeOffset? to,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Orders.AsNoTracking().Include(o => o.Items).AsQueryable();
        if (status.HasValue)
        {
            query = query.Where(o => o.Status == status.Value);
        }
        if (from.HasValue)
        {
            query = query.Where(o => o.CreatedAt >= from.Value);
        }
        if (to.HasValue)
        {
            query = query.Where(o => o.CreatedAt <= to.Value);
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(o =>
                o.OrderNumber.ToLower().Contains(term) ||
                o.CustomerName.ToLower().Contains(term) ||
                o.ContactEmail.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var orders = await query
            .OrderByDescending(o => o.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var orderIds = orders.Select(o => o.Id).ToList();
        var payments = await db.Payments.AsNoTracking().Where(p => orderIds.Contains(p.OrderId)).ToDictionaryAsync(p => p.OrderId, ct);

        var items = orders.Select(o => new AdminOrderSummaryDto(
            o.Id.ToString(),
            o.OrderNumber,
            o.Status,
            payments.TryGetValue(o.Id, out var p) ? p.Status : null,
            o.DeliveryMethod,
            o.Total,
            o.Currency,
            o.Items.Sum(i => i.Quantity),
            o.CreatedAt,
            o.CustomerName,
            o.ContactEmail)).ToList();

        return PagedResult<AdminOrderSummaryDto>.Create(items, page, pageSize, total);
    }

    public async Task<AdminOrderDetailDto> GetAdminOrderDetailAsync(Guid orderId, bool isAdmin, CancellationToken ct = default)
    {
        var order = await db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        return await BuildAdminOrderDetailDtoAsync(order, isAdmin, ct);
    }

    public async Task<AdminOrderDetailDto> UpdateOrderStatusByStaffAsync(
        Guid orderId,
        OrderStatus nextStatus,
        string? note,
        string? actor,
        bool isAdmin,
        CancellationToken ct = default)
    {
        if (nextStatus is not (OrderStatus.Processing or OrderStatus.ReadyForDispatch or OrderStatus.Delivered))
        {
            throw new DomainException("Staff status endpoint only permits Processing, ReadyForDispatch, or Delivered (for pickup).", "validation_error", 400);
        }

        var order = await db.Orders
            .Include(o => o.Items)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == order.Id, ct);
        if (payment?.Status != PaymentStatus.Successful)
        {
            throw DomainException.Conflict("Order cannot be prepared or dispatched until payment has been independently verified.", "payment_not_verified");
        }

        if (nextStatus == OrderStatus.Delivered && order.DeliveryMethod != DeliveryMethod.Pickup)
        {
            throw DomainException.Conflict("Home delivery orders must be completed through the delivery operations workflow.", "invalid_state_transition");
        }

        var hist = order.TransitionTo(nextStatus, note ?? $"Order marked {nextStatus}.", actor);
        db.OrderStatusHistories.Add(hist);
        audit.Record("OrderStatusTransition", "Order", order.Id.ToString(), $"Order {order.OrderNumber} -> {nextStatus}");
        await db.SaveChangesAsync(ct);

        await notifications.QueueAsync(
            order.UserId,
            order.ContactEmail,
            $"Order{nextStatus}",
            $"Order {order.OrderNumber} is {nextStatus}",
            nextStatus switch
            {
                OrderStatus.Processing => $"Our warehouse team is now packing your drinks order {order.OrderNumber}.",
                OrderStatus.ReadyForDispatch => order.DeliveryMethod == DeliveryMethod.Pickup
                    ? $"Your order {order.OrderNumber} is ready for pickup at our store."
                    : $"Your order {order.OrderNumber} is packed and ready for dispatch.",
                OrderStatus.Delivered => $"Your pickup order {order.OrderNumber} has been collected. Enjoy your drinks!",
                _ => $"Order {order.OrderNumber} status updated to {nextStatus}."
            },
            order.Id,
            ct);

        return await BuildAdminOrderDetailDtoAsync(order, isAdmin, ct);
    }

    public async Task<AdminOrderDetailDto> ApproveCancellationAsync(Guid orderId, string? note, string? actor, CancellationToken ct = default)
    {
        var order = await db.Orders
            .Include(o => o.Items)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        if (order.Status != OrderStatus.CancellationRequested)
        {
            throw DomainException.Conflict("Order does not have a pending cancellation request.", "invalid_state_transition");
        }

        var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == order.Id, ct);

        // Return committed stock to on-hand inventory
        foreach (var item in order.Items)
        {
            var inv = await db.InventoryItems.FirstOrDefaultAsync(i => i.VariantId == item.VariantId, ct);
            if (inv is not null)
            {
                db.InventoryTransactions.Add(inv.ReturnCommittedStock(item.Quantity, order.Id, $"Cancellation approved for order {order.OrderNumber}.", actor));
            }
        }

        if (payment?.Status == PaymentStatus.Successful)
        {
            var hist = order.TransitionTo(OrderStatus.RefundPending, note ?? "Cancellation approved; order queued for refund.", actor);
            db.OrderStatusHistories.Add(hist);
        }
        else
        {
            var hist = order.TransitionTo(OrderStatus.Cancelled, note ?? "Cancellation approved.", actor);
            db.OrderStatusHistories.Add(hist);
        }

        audit.Record("ApproveOrderCancellation", "Order", order.Id.ToString(), $"Approved cancellation for {order.OrderNumber}");
        await db.SaveChangesAsync(ct);

        await notifications.QueueAsync(
            order.UserId,
            order.ContactEmail,
            "CancellationApproved",
            $"Cancellation Approved for Order {order.OrderNumber}",
            $"Your cancellation request for order {order.OrderNumber} has been approved.",
            order.Id,
            ct);

        return await BuildAdminOrderDetailDtoAsync(order, true, ct);
    }

    public async Task<AdminOrderDetailDto> RejectCancellationAsync(Guid orderId, string? note, string? actor, CancellationToken ct = default)
    {
        var order = await db.Orders
            .Include(o => o.Items)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        var hist = order.RejectCancellation(note, actor);
        db.OrderStatusHistories.Add(hist);
        audit.Record("RejectOrderCancellation", "Order", order.Id.ToString(), $"Rejected cancellation for {order.OrderNumber}");
        await db.SaveChangesAsync(ct);

        await notifications.QueueAsync(
            order.UserId,
            order.ContactEmail,
            "CancellationRejected",
            $"Cancellation Request Update for Order {order.OrderNumber}",
            $"Your cancellation request for order {order.OrderNumber} could not be approved: {note ?? "Order is already being prepared."}",
            order.Id,
            ct);

        return await BuildAdminOrderDetailDtoAsync(order, true, ct);
    }

    // Delivery Operations
    public async Task<PagedResult<DeliveryDto>> GetDeliveriesAsync(
        DeliveryStatus? status,
        Guid? callerUserId,
        bool isAdmin,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.OrderDeliveries
            .AsNoTracking()
            .Include(d => d.History)
            .AsQueryable();

        if (!isAdmin && callerUserId.HasValue)
        {
            query = query.Where(d => d.AssignedToUserId == null || d.AssignedToUserId == callerUserId.Value);
        }

        if (status.HasValue)
        {
            query = query.Where(d => d.Status == status.Value);
        }

        var total = await query.CountAsync(ct);
        var list = await query
            .OrderByDescending(d => d.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var orderIds = list.Select(d => d.OrderId).ToList();
        var orders = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);

        var dtos = list
            .Where(d => orders.ContainsKey(d.OrderId))
            .Select(d => MapDelivery(d, orders[d.OrderId]))
            .ToList();

        return PagedResult<DeliveryDto>.Create(dtos, page, pageSize, total);
    }

    public async Task<DeliveryDto> AssignDeliveryAsync(Guid orderId, AssignDeliveryRequest request, string? actor, CancellationToken ct = default)
    {
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        var delivery = await db.OrderDeliveries
            .Include(d => d.History)
            .FirstOrDefaultAsync(d => d.OrderId == orderId, ct)
            ?? throw DomainException.NotFound("Delivery record not found for order.");

        var hist = delivery.AssignRider(request.AssignedToUserId, request.RiderName, request.RiderPhone, request.ScheduledFor, actor);
        db.DeliveryStatusHistories.Add(hist);
        audit.Record("AssignDelivery", "OrderDelivery", delivery.Id.ToString(), $"Assigned order {order.OrderNumber} to {delivery.RiderName}");
        await db.SaveChangesAsync(ct);

        return MapDelivery(delivery, order);
    }

    public async Task<DeliveryDto> DispatchDeliveryAsync(Guid orderId, string? note, string? actor, CancellationToken ct = default)
    {
        var order = await db.Orders
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == order.Id, ct);
        if (payment?.Status != PaymentStatus.Successful)
        {
            throw DomainException.Conflict("Order cannot be dispatched without a verified payment.", "payment_not_verified");
        }

        if (order.Status is not (OrderStatus.ReadyForDispatch or OrderStatus.DeliveryFailed))
        {
            throw DomainException.Conflict($"Order must be ReadyForDispatch before dispatching (current: {order.Status}).", "invalid_state_transition");
        }

        var delivery = await db.OrderDeliveries
            .Include(d => d.History)
            .FirstOrDefaultAsync(d => d.OrderId == orderId, ct)
            ?? throw DomainException.NotFound("Delivery record not found.");

        var delHist = delivery.Dispatch(note, actor);
        db.DeliveryStatusHistories.Add(delHist);

        var ordHist = order.TransitionTo(OrderStatus.OutForDelivery, $"Out for delivery with rider {delivery.RiderName} ({delivery.RiderPhone}).", actor);
        db.OrderStatusHistories.Add(ordHist);

        audit.Record("DispatchOrder", "Order", order.Id.ToString(), $"Dispatched order {order.OrderNumber} with rider {delivery.RiderName}.");
        await db.SaveChangesAsync(ct);

        await notifications.QueueAsync(
            order.UserId,
            order.ContactEmail,
            "OrderDispatched",
            $"Order {order.OrderNumber} is Out for Delivery!",
            $"Your drinks order {order.OrderNumber} is on its way with rider {delivery.RiderName} ({delivery.RiderPhone}).",
            order.Id,
            ct);

        return MapDelivery(delivery, order);
    }

    public async Task<DeliveryDto> CompleteDeliveryAsync(Guid orderId, string? receivedBy, string? note, string? actor, CancellationToken ct = default)
    {
        var order = await db.Orders
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == order.Id, ct);
        if (payment?.Status != PaymentStatus.Successful)
        {
            throw DomainException.Conflict("Delivery completion cannot be recorded for an order without verified payment.", "payment_not_verified");
        }

        var delivery = await db.OrderDeliveries
            .Include(d => d.History)
            .FirstOrDefaultAsync(d => d.OrderId == orderId, ct)
            ?? throw DomainException.NotFound("Delivery record not found.");

        var delHist = delivery.MarkDelivered(receivedBy, note, actor);
        db.DeliveryStatusHistories.Add(delHist);

        var ordHist = order.TransitionTo(OrderStatus.Delivered, $"Delivered and received by {delivery.ReceivedBy}.", actor);
        db.OrderStatusHistories.Add(ordHist);

        audit.Record("CompleteDelivery", "Order", order.Id.ToString(), $"Confirmed delivery of order {order.OrderNumber} to {delivery.ReceivedBy}.");
        await db.SaveChangesAsync(ct);

        await notifications.QueueAsync(
            order.UserId,
            order.ContactEmail,
            "OrderDelivered",
            $"Order {order.OrderNumber} Delivered",
            $"Your order {order.OrderNumber} has been delivered and received by {delivery.ReceivedBy}. Thank you for shopping with DrinksEcommerce!",
            order.Id,
            ct);

        return MapDelivery(delivery, order);
    }

    public async Task<DeliveryDto> RecordDeliveryExceptionAsync(Guid orderId, string reason, string? note, string? actor, CancellationToken ct = default)
    {
        var order = await db.Orders
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        var delivery = await db.OrderDeliveries
            .Include(d => d.History)
            .FirstOrDefaultAsync(d => d.OrderId == orderId, ct)
            ?? throw DomainException.NotFound("Delivery record not found.");

        var delHist = delivery.RecordException(reason, note, actor);
        db.DeliveryStatusHistories.Add(delHist);

        if (order.Status == OrderStatus.OutForDelivery)
        {
            var ordHist = order.TransitionTo(OrderStatus.DeliveryFailed, $"Delivery attempt exception ({reason}): {note}", actor);
            db.OrderStatusHistories.Add(ordHist);
        }

        audit.Record("DeliveryException", "OrderDelivery", delivery.Id.ToString(), $"Recorded exception ({reason}) on {order.OrderNumber}.");
        await db.SaveChangesAsync(ct);

        await notifications.QueueAsync(
            order.UserId,
            order.ContactEmail,
            "DeliveryException",
            $"Delivery Update for Order {order.OrderNumber}",
            $"We encountered a delivery exception ({reason}) for order {order.OrderNumber}. Our team will follow up to reschedule.",
            order.Id,
            ct);

        return MapDelivery(delivery, order);
    }

    // Analytics Summary
    public async Task<SalesSummaryDto> GetSalesSummaryAsync(DateOnly? fromDate, DateOnly? toDate, CancellationToken ct = default)
    {
        var lagosZone = TimeZoneInfo.FindSystemTimeZoneById("W. Central Africa Standard Time");
        var nowLagos = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, lagosZone);
        var to = toDate ?? DateOnly.FromDateTime(nowLagos.DateTime);
        var from = fromDate ?? to.AddDays(-29);

        var startUtc = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), lagosZone.BaseUtcOffset).ToUniversalTime();
        var endUtc = new DateTimeOffset(to.ToDateTime(TimeOnly.MaxValue), lagosZone.BaseUtcOffset).ToUniversalTime();

        var orders = await db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.CreatedAt >= startUtc && o.CreatedAt <= endUtc)
            .ToListAsync(ct);

        var payments = await db.Payments
            .AsNoTracking()
            .Where(p => p.CreatedAt >= startUtc && p.CreatedAt <= endUtc)
            .ToListAsync(ct);

        var paidPaymentOrderIds = payments
            .Where(p => p.Status is PaymentStatus.Successful or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded or PaymentStatus.RefundPending)
            .Select(p => p.OrderId)
            .ToHashSet();

        var paidOrders = orders.Where(o => paidPaymentOrderIds.Contains(o.Id)).ToList();
        var grossRevenue = paidOrders.Sum(o => o.Total);
        var refundedAmount = payments.Sum(p => p.RefundedAmount);
        var netRevenue = Math.Max(0m, grossRevenue - refundedAmount);
        var avgOrderValue = paidOrders.Count > 0 ? NigerianValueRules.RoundNaira(grossRevenue / paidOrders.Count) : 0m;
        var pendingPaymentOrders = orders.Count(o => o.Status == OrderStatus.PendingPayment);

        var ordersByStatus = orders
            .GroupBy(o => o.Status.ToString())
            .Select(g => new StatusCountDto(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        var topProducts = paidOrders
            .SelectMany(o => o.Items)
            .GroupBy(i => new { i.ProductName, i.VariantName, i.Sku })
            .Select(g => new TopProductSalesDto(
                g.Key.ProductName,
                g.Key.VariantName,
                g.Key.Sku,
                g.Sum(x => x.Quantity),
                NigerianValueRules.RoundNaira(g.Sum(x => x.LineTotal))))
            .OrderByDescending(x => x.Revenue)
            .Take(10)
            .ToList();

        var dailySales = paidOrders
            .GroupBy(o => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.CreatedAt, lagosZone).DateTime).ToString("yyyy-MM-dd"))
            .Select(g => new DailySalesDto(g.Key, g.Count(), NigerianValueRules.RoundNaira(g.Sum(x => x.Total))))
            .OrderBy(d => d.Date)
            .ToList();

        var lowStockCount = await db.InventoryItems.CountAsync(i => (i.OnHand - i.Reserved) <= i.LowStockThreshold, ct);
        var openReconciliationCount = await db.ReconciliationRecords.CountAsync(r => r.Status == ReconciliationStatus.Open, ct);

        return new SalesSummaryDto(
            from.ToString("yyyy-MM-dd"),
            to.ToString("yyyy-MM-dd"),
            NigerianValueRules.RoundNaira(grossRevenue),
            NigerianValueRules.RoundNaira(refundedAmount),
            NigerianValueRules.RoundNaira(netRevenue),
            paidOrders.Count,
            avgOrderValue,
            pendingPaymentOrders,
            ordersByStatus,
            topProducts,
            dailySales,
            lowStockCount,
            openReconciliationCount);
    }

    // Helpers
    private async Task<OrderDetailDto> BuildOrderDetailDtoAsync(Order order, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == order.Id, ct);
        var delivery = await db.OrderDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.OrderId == order.Id, ct);
        PickupLocationDto? pickup = null;
        if (order.PickupLocationId.HasValue)
        {
            var pLoc = await db.PickupLocations.AsNoTracking().FirstOrDefaultAsync(p => p.Id == order.PickupLocationId.Value, ct);
            if (pLoc is not null)
            {
                pickup = new PickupLocationDto(pLoc.Id.ToString(), pLoc.Name, pLoc.Address, pLoc.State, pLoc.City, pLoc.OpeningHours, pLoc.PhoneNumber);
            }
        }

        return new OrderDetailDto(
            order.Id.ToString(),
            order.OrderNumber,
            order.Status,
            payment?.Status,
            delivery?.Status,
            order.DeliveryMethod,
            order.Items.Select(i => new OrderItemDto(
                i.Id.ToString(),
                i.ProductId.ToString(),
                i.VariantId.ToString(),
                i.ProductName,
                i.VariantName,
                i.Sku,
                i.ImageUrl,
                i.UnitPrice,
                i.Quantity,
                i.LineTotal)).ToList(),
            order.Subtotal,
            order.Discount,
            order.DeliveryFee,
            order.Tax,
            order.Total,
            order.Currency,
            order.CouponCode,
            order.AddressSnapshot is null ? null : new OrderAddressDto(
                order.AddressSnapshot.RecipientName,
                order.AddressSnapshot.PhoneNumber,
                order.AddressSnapshot.State,
                order.AddressSnapshot.City,
                order.AddressSnapshot.Street,
                order.AddressSnapshot.Landmark,
                order.AddressSnapshot.PostalCode),
            pickup,
            order.DeliveryInstructions,
            order.ContactEmail,
            payment is null ? null : new OrderPaymentSummaryDto(payment.Reference, payment.Status, payment.Amount, payment.PaidAt, payment.Channel),
            order.StatusHistory.OrderByDescending(h => h.CreatedAt).Select(h => new StatusHistoryDto(h.Status, h.Note, h.Actor, h.CreatedAt)).ToList(),
            order.CanCustomerCancel,
            order.CanPay,
            order.ReservationExpiresAt,
            order.CreatedAt);
    }

    private async Task<AdminOrderDetailDto> BuildAdminOrderDetailDtoAsync(Order order, bool isAdmin, CancellationToken ct)
    {
        var baseDetail = await BuildOrderDetailDtoAsync(order, ct);
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.OrderId == order.Id, ct);
        var delivery = await db.OrderDeliveries
            .AsNoTracking()
            .Include(d => d.History)
            .FirstOrDefaultAsync(d => d.OrderId == order.Id, ct);

        var allowed = new List<string>();
        if (payment?.Status == PaymentStatus.Successful)
        {
            if (order.Status == OrderStatus.Confirmed)
            {
                allowed.Add(OrderStatus.Processing.ToString());
            }
            if (order.Status == OrderStatus.Processing)
            {
                allowed.Add(OrderStatus.ReadyForDispatch.ToString());
            }
            if (order.Status == OrderStatus.ReadyForDispatch && order.DeliveryMethod == DeliveryMethod.Pickup)
            {
                allowed.Add(OrderStatus.Delivered.ToString());
            }
        }
        if (isAdmin && order.Status == OrderStatus.CancellationRequested)
        {
            allowed.Add("CancelApprove");
            allowed.Add("CancelReject");
        }

        return new AdminOrderDetailDto(
            baseDetail.Id,
            baseDetail.OrderNumber,
            baseDetail.Status,
            baseDetail.PaymentStatus,
            baseDetail.DeliveryStatus,
            baseDetail.DeliveryMethod,
            baseDetail.Items,
            baseDetail.Subtotal,
            baseDetail.Discount,
            baseDetail.DeliveryFee,
            baseDetail.Tax,
            baseDetail.Total,
            baseDetail.Currency,
            baseDetail.CouponCode,
            baseDetail.Address,
            baseDetail.PickupLocation,
            baseDetail.DeliveryInstructions,
            baseDetail.ContactEmail,
            baseDetail.Payment,
            baseDetail.StatusHistory,
            baseDetail.CanCancel,
            baseDetail.CanPay,
            baseDetail.ReservationExpiresAt,
            baseDetail.CreatedAt,
            order.UserId.ToString(),
            order.CustomerName,
            order.ContactEmail,
            order.CustomerPhone,
            delivery is null ? null : MapDelivery(delivery, order),
            allowed,
            payment?.RefundedAmount ?? 0m,
            payment?.RefundableAmount ?? 0m);
    }

    private static DeliveryDto MapDelivery(OrderDelivery d, Order o) =>
        new(
            d.Id.ToString(),
            d.OrderId.ToString(),
            o.OrderNumber,
            d.Status,
            o.Status,
            d.RecipientName,
            d.PhoneNumber,
            d.AddressLine,
            d.State,
            d.City,
            d.DeliveryInstructions,
            d.AssignedToUserId?.ToString(),
            d.RiderName,
            d.RiderPhone,
            d.ScheduledFor,
            d.DispatchedAt,
            d.DeliveredAt,
            d.ReceivedBy,
            d.AttemptCount,
            d.LastException,
            d.UpdatedAt,
            d.History.OrderByDescending(h => h.CreatedAt).Select(h => new StatusHistoryDto(h.Status.ToString(), h.Note, h.Actor, h.CreatedAt)).ToList());

    private static InventoryItemDto MapInventoryItem(InventoryItem i) =>
        new(
            i.VariantId.ToString(),
            i.Variant!.ProductId.ToString(),
            i.Variant.Product!.Name,
            i.Variant.Name,
            i.Variant.Sku,
            i.OnHand,
            i.Reserved,
            i.Available,
            i.LowStockThreshold,
            i.Available <= i.LowStockThreshold,
            i.UpdatedAt);

    private static AdminProductDto MapAdminProduct(Product p) =>
        new(
            p.Id.ToString(),
            p.Slug,
            p.Name,
            p.Description,
            p.Brand,
            p.CategoryId.ToString(),
            p.Category?.Name ?? string.Empty,
            p.Category?.Slug ?? string.Empty,
            p.IsAlcoholic,
            p.IsFeatured,
            p.IsActive,
            p.CreatedAt,
            p.UpdatedAt,
            p.Images.OrderBy(i => i.SortOrder).Select(i => new ProductImageDto(i.Id.ToString(), i.Url, i.AltText, i.SortOrder)).ToList(),
            p.Attributes.OrderBy(a => a.Name).Select(a => new ProductAttributeDto(a.Name, a.Value)).ToList(),
            p.Variants.OrderBy(v => v.UnitsPerPack).Select(v =>
            {
                var baseVar = CatalogService.MapVariant(v);
                var onHand = v.Inventory?.OnHand ?? 0;
                var reserved = v.Inventory?.Reserved ?? 0;
                var avail = v.Inventory?.Available ?? 0;
                var threshold = v.Inventory?.LowStockThreshold ?? 10;
                return new AdminVariantDto(
                    baseVar.Id,
                    baseVar.Sku,
                    baseVar.Name,
                    baseVar.PackType,
                    baseVar.UnitsPerPack,
                    baseVar.UnitVolumeMl,
                    baseVar.Price,
                    baseVar.CompareAtPrice,
                    baseVar.StockStatus,
                    baseVar.AvailableQuantity,
                    baseVar.IsActive,
                    onHand,
                    reserved,
                    avail,
                    threshold);
            }).ToList());
}
