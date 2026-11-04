using DrinksEcommerce.Application.Common;
using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Application.Contracts;
using DrinksEcommerce.Application.Orders;
using DrinksEcommerce.Application.Payments;
using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Customers;
using DrinksEcommerce.Domain.Delivery;
using DrinksEcommerce.Domain.Enums;
using DrinksEcommerce.Persistence;
using DrinksEcommerce.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace DrinksEcommerce.API.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").RequireAuthorization();

        // 1. Products (Administrator write, WarehouseStaff read)
        admin.MapGet("/products", async (
            string? search,
            Guid? categoryId,
            bool? includeInactive,
            int? page,
            int? pageSize,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            var res = await adminService.GetAdminProductsAsync(search, categoryId, includeInactive ?? true, page ?? 1, pageSize ?? 20, ct);
            return Results.Ok(res);
        }).RequireAuthorization("WarehouseOrAdmin");

        admin.MapGet("/products/{id:guid}", async (
            Guid id,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.GetAdminProductByIdAsync(id, ct));
        }).RequireAuthorization("WarehouseOrAdmin");

        admin.MapPost("/products", async (
            [FromBody] CreateProductRequest request,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            var created = await adminService.CreateProductAsync(request, currentUser.Email, ct);
            return Results.Created($"/api/v1/admin/products/{created.Id}", created);
        }).RequireAuthorization("AdminOnly");

        admin.MapPut("/products/{id:guid}", async (
            Guid id,
            [FromBody] UpdateProductRequest request,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.UpdateProductAsync(id, request, currentUser.Email, ct));
        }).RequireAuthorization("AdminOnly");

        admin.MapDelete("/products/{id:guid}", async (
            Guid id,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            await adminService.DeactivateProductAsync(id, currentUser.Email, ct);
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");

        admin.MapPost("/products/{id:guid}/variants", async (
            Guid id,
            [FromBody] VariantInput input,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.AddVariantAsync(id, input, currentUser.Email, ct));
        }).RequireAuthorization("AdminOnly");

        admin.MapPut("/products/{id:guid}/variants/{variantId:guid}", async (
            Guid id,
            Guid variantId,
            [FromBody] VariantInput input,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.UpdateVariantAsync(id, variantId, input, currentUser.Email, ct));
        }).RequireAuthorization("AdminOnly");

        admin.MapPost("/products/{id:guid}/images", async (
            Guid id,
            HttpRequest httpRequest,
            IWebHostEnvironment env,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            if (!httpRequest.HasFormContentType)
            {
                throw new DomainException("Multipart form-data is required for image uploads.", "validation_error", 400);
            }

            var form = await httpRequest.ReadFormAsync(ct);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault()
                ?? throw new DomainException("Image file is required.", "validation_error", 400);

            if (file.Length <= 0 || file.Length > 2 * 1024 * 1024)
            {
                throw new DomainException("Image file must be between 1 byte and 2 MB.", "validation_error", 400);
            }

            await using var stream = file.OpenReadStream();
            var header = new byte[12];
            var read = await stream.ReadAsync(header.AsMemory(0, header.Length), ct);
            var ext = DetectSafeImageExtension(header, read)
                ?? throw new DomainException("Only valid JPEG, PNG, or WebP images are permitted.", "validation_error", 400);

            stream.Position = 0;
            var webRoot = env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot");
            var uploadsDir = Path.Combine(webRoot, "uploads", "products");
            Directory.CreateDirectory(uploadsDir);

            var fileName = $"{id:N}_{Guid.NewGuid():N}{ext}";
            var fullPath = Path.Combine(uploadsDir, fileName);
            await using (var outStream = File.Create(fullPath))
            {
                await stream.CopyToAsync(outStream, ct);
            }

            var altText = form["altText"].FirstOrDefault();
            var relativeUrl = $"/uploads/products/{fileName}";
            var updated = await adminService.AddProductImageAsync(id, relativeUrl, altText, currentUser.Email, ct);
            return Results.Ok(updated);
        }).RequireAuthorization("AdminOnly");

        admin.MapDelete("/products/{id:guid}/images/{imageId:guid}", async (
            Guid id,
            Guid imageId,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.DeleteProductImageAsync(id, imageId, currentUser.Email, ct));
        }).RequireAuthorization("AdminOnly");

        // 2. Categories (Administrator)
        admin.MapGet("/categories", async (OrderAndAdminService adminService, CancellationToken ct) =>
            Results.Ok(await adminService.GetAdminCategoriesAsync(ct))).RequireAuthorization("WarehouseOrAdmin");

        admin.MapPost("/categories", async (
            [FromBody] CategoryInput input,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            var created = await adminService.CreateCategoryAsync(input, currentUser.Email, ct);
            return Results.Created($"/api/v1/admin/categories/{created.Id}", created);
        }).RequireAuthorization("AdminOnly");

        admin.MapPut("/categories/{id:guid}", async (
            Guid id,
            [FromBody] CategoryInput input,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.UpdateCategoryAsync(id, input, currentUser.Email, ct));
        }).RequireAuthorization("AdminOnly");

        admin.MapDelete("/categories/{id:guid}", async (
            Guid id,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            await adminService.DeleteCategoryAsync(id, currentUser.Email, ct);
            return Results.NoContent();
        }).RequireAuthorization("AdminOnly");

        // 3. Inventory (Administrator, WarehouseStaff)
        var inv = admin.MapGroup("/inventory").RequireAuthorization("WarehouseOrAdmin");

        inv.MapGet("", async (
            string? search,
            bool? lowStockOnly,
            int? page,
            int? pageSize,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.GetInventoryAsync(search, lowStockOnly == true, page ?? 1, pageSize ?? 25, ct));
        });

        inv.MapPost("/{variantId:guid}/adjust", async (
            Guid variantId,
            [FromBody] AdjustInventoryRequest request,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.AdjustInventoryAsync(variantId, request, currentUser.Email, ct));
        });

        inv.MapPut("/{variantId:guid}/threshold", async (
            Guid variantId,
            [FromBody] UpdateThresholdRequest request,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.UpdateThresholdAsync(variantId, request.LowStockThreshold, currentUser.Email, ct));
        });

        inv.MapGet("/{variantId:guid}/transactions", async (
            Guid variantId,
            int? page,
            int? pageSize,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.GetInventoryTransactionsAsync(variantId, page ?? 1, pageSize ?? 20, ct));
        });

        // 4. Orders (Administrator, WarehouseStaff)
        var ord = admin.MapGroup("/orders").RequireAuthorization("WarehouseOrAdmin");

        ord.MapGet("", async (
            OrderStatus? status,
            string? search,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? page,
            int? pageSize,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.GetAdminOrdersAsync(status, search, from, to, page ?? 1, pageSize ?? 20, ct));
        });

        ord.MapGet("/{id:guid}", async (
            Guid id,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.GetAdminOrderDetailAsync(id, currentUser.IsInRole(Roles.Administrator), ct));
        });

        ord.MapPost("/{id:guid}/status", async (
            Guid id,
            [FromBody] UpdateOrderStatusRequest request,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            var isAdmin = currentUser.IsInRole(Roles.Administrator);
            return Results.Ok(await adminService.UpdateOrderStatusByStaffAsync(id, request.Status, request.Note, currentUser.Email, isAdmin, ct));
        });

        ord.MapPost("/{id:guid}/cancellation/approve", async (
            Guid id,
            [FromBody] OrderNoteRequest? request,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.ApproveCancellationAsync(id, request?.Note, currentUser.Email, ct));
        }).RequireAuthorization("AdminOnly");

        ord.MapPost("/{id:guid}/cancellation/reject", async (
            Guid id,
            [FromBody] OrderNoteRequest? request,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.RejectCancellationAsync(id, request?.Note, currentUser.Email, ct));
        }).RequireAuthorization("AdminOnly");

        // 5. Deliveries (Administrator, DeliveryStaff)
        var del = admin.MapGroup("/deliveries").RequireAuthorization("DeliveryOrAdmin");

        del.MapGet("", async (
            DeliveryStatus? status,
            int? page,
            int? pageSize,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            var isAdmin = currentUser.IsInRole(Roles.Administrator);
            return Results.Ok(await adminService.GetDeliveriesAsync(status, currentUser.UserId, isAdmin, page ?? 1, pageSize ?? 20, ct));
        });

        del.MapGet("/staff", async (UserManager<ApplicationUser> userManager) =>
        {
            var riders = await userManager.GetUsersInRoleAsync(Roles.DeliveryStaff);
            var dtos = riders.Select(r => new DeliveryStaffDto(r.Id.ToString(), r.FullName, r.Email ?? string.Empty, r.PhoneNumber)).ToList();
            return Results.Ok(dtos);
        }).RequireAuthorization("AdminOnly");

        del.MapPost("/{orderId:guid}/assign", async (
            Guid orderId,
            [FromBody] AssignDeliveryRequest request,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.AssignDeliveryAsync(orderId, request, currentUser.Email, ct));
        }).RequireAuthorization("AdminOnly");

        del.MapPost("/{orderId:guid}/dispatch", async (
            Guid orderId,
            [FromBody] DispatchDeliveryRequest? request,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.DispatchDeliveryAsync(orderId, request?.Note, currentUser.Email, ct));
        });

        del.MapPost("/{orderId:guid}/deliver", async (
            Guid orderId,
            [FromBody] CompleteDeliveryRequest? request,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.CompleteDeliveryAsync(orderId, request?.ReceivedBy, request?.Note, currentUser.Email, ct));
        });

        del.MapPost("/{orderId:guid}/exception", async (
            Guid orderId,
            [FromBody] DeliveryExceptionRequest request,
            ICurrentUserService currentUser,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            return Results.Ok(await adminService.RecordDeliveryExceptionAsync(orderId, request.Reason, request.Note, currentUser.Email, ct));
        });

        // 6. Payments, Refunds & Reconciliation (Administrator only)
        var fin = admin.MapGroup("").RequireAuthorization("AdminOnly");

        fin.MapGet("/payments", async (
            PaymentStatus? status,
            string? search,
            int? page,
            int? pageSize,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 20, 1, 100);

            var query = db.Payments.AsNoTracking().Include(x => x.Attempts).AsQueryable();
            if (status.HasValue)
            {
                query = query.Where(x => x.Status == status.Value);
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLowerInvariant();
                query = query.Where(x =>
                    x.Reference.ToLower().Contains(term) ||
                    x.OrderNumber.ToLower().Contains(term) ||
                    x.CustomerEmail.ToLower().Contains(term));
            }

            var total = await query.CountAsync(ct);
            var list = await query
                .OrderByDescending(x => x.CreatedAt)
                .Skip((p - 1) * ps)
                .Take(ps)
                .Select(x => new AdminPaymentDto(
                    x.Id.ToString(),
                    x.Reference,
                    x.OrderId.ToString(),
                    x.OrderNumber,
                    x.Status,
                    x.Amount,
                    x.Currency,
                    x.Channel,
                    x.PaidAt,
                    x.CreatedAt,
                    x.RefundedAmount,
                    x.CustomerEmail,
                    x.Attempts.Count))
                .ToListAsync(ct);

            return Results.Ok(PagedResult<AdminPaymentDto>.Create(list, p, ps, total));
        });

        fin.MapGet("/payments/{id:guid}", async (Guid id, ApplicationDbContext db, CancellationToken ct) =>
        {
            var x = await db.Payments
                .AsNoTracking()
                .Include(p => p.Attempts)
                .Include(p => p.Refunds)
                .Include(p => p.History)
                .FirstOrDefaultAsync(p => p.Id == id, ct)
                ?? throw DomainException.NotFound("Payment not found.");

            var dto = new AdminPaymentDetailDto(
                x.Id.ToString(),
                x.Reference,
                x.OrderId.ToString(),
                x.OrderNumber,
                x.Status,
                x.Amount,
                x.Currency,
                x.Channel,
                x.PaidAt,
                x.CreatedAt,
                x.RefundedAmount,
                x.CustomerEmail,
                x.Attempts.Count,
                x.GatewayResponse,
                x.ProviderTransactionId,
                x.Attempts.OrderByDescending(a => a.CreatedAt).Select(a => new PaymentAttemptDto(a.Id.ToString(), a.Reference, a.Status, a.AuthorizationUrl, a.ErrorMessage, a.CreatedAt)).ToList(),
                x.Refunds.OrderByDescending(r => r.CreatedAt).Select(PaymentOrchestratorService.MapRefund).ToList(),
                x.History.OrderByDescending(h => h.CreatedAt).Select(h => new StatusHistoryDto(h.Status, h.Note, h.Actor, h.CreatedAt)).ToList());

            return Results.Ok(dto);
        });

        fin.MapPost("/payments/{reference}/reverify", async (
            string reference,
            ICurrentUserService currentUser,
            PaymentOrchestratorService orchestrator,
            CancellationToken ct) =>
        {
            return Results.Ok(await orchestrator.VerifyAndApplyPaymentAsync(currentUser.UserId, true, reference, ct));
        });

        fin.MapGet("/refunds", async (
            RefundStatus? status,
            int? page,
            int? pageSize,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 20, 1, 100);
            var query = db.Refunds.AsNoTracking().AsQueryable();
            if (status.HasValue)
            {
                query = query.Where(r => r.Status == status.Value);
            }

            var total = await query.CountAsync(ct);
            var list = await query
                .OrderByDescending(r => r.CreatedAt)
                .Skip((p - 1) * ps)
                .Take(ps)
                .ToListAsync(ct);

            return Results.Ok(PagedResult<RefundDto>.Create(list.Select(PaymentOrchestratorService.MapRefund).ToList(), p, ps, total));
        });

        fin.MapPost("/refunds", async (
            [FromBody] CreateRefundRequest request,
            ICurrentUserService currentUser,
            PaymentOrchestratorService orchestrator,
            CancellationToken ct) =>
        {
            var created = await orchestrator.RequestRefundAsync(request.OrderId, request.Amount, request.Reason, currentUser.Email, ct);
            return Results.Created($"/api/v1/admin/refunds/{created.Id}", created);
        });

        fin.MapPost("/refunds/{id:guid}/sync", async (
            Guid id,
            ICurrentUserService currentUser,
            PaymentOrchestratorService orchestrator,
            CancellationToken ct) =>
        {
            return Results.Ok(await orchestrator.SyncRefundStatusAsync(id, currentUser.Email, ct));
        });

        fin.MapGet("/reconciliation", async (
            ReconciliationStatus? status,
            ReconciliationType? type,
            int? page,
            int? pageSize,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 20, 1, 100);

            var query = db.ReconciliationRecords.AsNoTracking().AsQueryable();
            if (status.HasValue)
            {
                query = query.Where(r => r.Status == status.Value);
            }
            if (type.HasValue)
            {
                query = query.Where(r => r.Type == type.Value);
            }

            var total = await query.CountAsync(ct);
            var list = await query
                .OrderByDescending(r => r.CreatedAt)
                .Skip((p - 1) * ps)
                .Take(ps)
                .Select(r => new ReconciliationRecordDto(
                    r.Id.ToString(),
                    r.Type,
                    r.Status,
                    r.PaymentReference,
                    r.OrderId == null ? null : r.OrderId.ToString(),
                    r.OrderNumber,
                    r.ExpectedAmount,
                    r.ActualAmount,
                    r.Currency,
                    r.Description,
                    r.CreatedAt,
                    r.ResolvedAt,
                    r.ResolvedBy,
                    r.ResolutionNote))
                .ToListAsync(ct);

            return Results.Ok(PagedResult<ReconciliationRecordDto>.Create(list, p, ps, total));
        });

        fin.MapPost("/reconciliation/run", async (
            ICurrentUserService currentUser,
            PaymentOrchestratorService orchestrator,
            CancellationToken ct) =>
        {
            return Results.Ok(await orchestrator.RunReconciliationAsync(currentUser.Email, ct));
        });

        fin.MapPost("/reconciliation/{id:guid}/resolve", async (
            Guid id,
            [FromBody] ResolveReconciliationRequest request,
            ICurrentUserService currentUser,
            IAuditService audit,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var rec = await db.ReconciliationRecords.FirstOrDefaultAsync(r => r.Id == id, ct)
                ?? throw DomainException.NotFound("Reconciliation record not found.");

            rec.Resolve(request.Note, currentUser.Email);
            audit.Record("ResolveReconciliation", "ReconciliationRecord", rec.Id.ToString(), request.Note);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new ReconciliationRecordDto(
                rec.Id.ToString(),
                rec.Type,
                rec.Status,
                rec.PaymentReference,
                rec.OrderId?.ToString(),
                rec.OrderNumber,
                rec.ExpectedAmount,
                rec.ActualAmount,
                rec.Currency,
                rec.Description,
                rec.CreatedAt,
                rec.ResolvedAt,
                rec.ResolvedBy,
                rec.ResolutionNote));
        });

        // 7. Coupons, Delivery Zones, Analytics & Audit Logs
        fin.MapGet("/coupons", async (ApplicationDbContext db, CancellationToken ct) =>
        {
            var list = await db.Coupons
                .AsNoTracking()
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new CouponDto(
                    c.Id.ToString(),
                    c.Code,
                    c.Description,
                    c.DiscountType,
                    c.Value,
                    c.MinOrderAmount,
                    c.MaxDiscountAmount,
                    c.StartsAt,
                    c.EndsAt,
                    c.UsageLimit,
                    c.PerCustomerLimit,
                    c.IsActive,
                    c.UsedCount))
                .ToListAsync(ct);
            return Results.Ok(list);
        });

        fin.MapPost("/coupons", async (
            [FromBody] CouponInput input,
            IAuditService audit,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var normCode = input.Code.Trim().ToUpperInvariant();
            if (await db.Coupons.AnyAsync(c => c.Code == normCode, ct))
            {
                throw DomainException.Conflict($"Coupon '{normCode}' already exists.");
            }

            var entity = new Coupon(
                normCode,
                input.Description,
                input.DiscountType,
                input.Value,
                input.MinOrderAmount,
                input.MaxDiscountAmount,
                input.StartsAt,
                input.EndsAt,
                input.UsageLimit,
                input.PerCustomerLimit,
                input.IsActive);

            db.Coupons.Add(entity);
            audit.Record("CreateCoupon", "Coupon", entity.Id.ToString(), $"Created coupon {entity.Code}");
            await db.SaveChangesAsync(ct);

            var dto = new CouponDto(entity.Id.ToString(), entity.Code, entity.Description, entity.DiscountType, entity.Value, entity.MinOrderAmount, entity.MaxDiscountAmount, entity.StartsAt, entity.EndsAt, entity.UsageLimit, entity.PerCustomerLimit, entity.IsActive, entity.UsedCount);
            return Results.Created($"/api/v1/admin/coupons/{dto.Id}", dto);
        });

        fin.MapPut("/coupons/{id:guid}", async (
            Guid id,
            [FromBody] CouponInput input,
            IAuditService audit,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var entity = await db.Coupons.FirstOrDefaultAsync(c => c.Id == id, ct)
                ?? throw DomainException.NotFound("Coupon not found.");

            entity.Update(
                input.Code,
                input.Description,
                input.DiscountType,
                input.Value,
                input.MinOrderAmount,
                input.MaxDiscountAmount,
                input.StartsAt,
                input.EndsAt,
                input.UsageLimit,
                input.PerCustomerLimit,
                input.IsActive);

            audit.Record("UpdateCoupon", "Coupon", entity.Id.ToString(), $"Updated coupon {entity.Code}");
            await db.SaveChangesAsync(ct);

            return Results.Ok(new CouponDto(entity.Id.ToString(), entity.Code, entity.Description, entity.DiscountType, entity.Value, entity.MinOrderAmount, entity.MaxDiscountAmount, entity.StartsAt, entity.EndsAt, entity.UsageLimit, entity.PerCustomerLimit, entity.IsActive, entity.UsedCount));
        });

        fin.MapGet("/delivery-zones", async (ApplicationDbContext db, CancellationToken ct) =>
        {
            var zones = await db.DeliveryZones
                .AsNoTracking()
                .Include(z => z.FeeRules)
                .OrderBy(z => z.State)
                .ThenBy(z => z.Name)
                .ToListAsync(ct);

            return Results.Ok(zones.Select(MapAdminDeliveryZone).ToList());
        });

        fin.MapPost("/delivery-zones", async (
            [FromBody] DeliveryZoneInput input,
            IAuditService audit,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var zone = new DeliveryZone(
                input.Name,
                input.State,
                input.Cities,
                input.BaseFee,
                input.FreeDeliveryThreshold,
                input.EstimatedMinDays,
                input.EstimatedMaxDays,
                input.IsActive);

            if (input.FeeRules is not null)
            {
                foreach (var r in input.FeeRules)
                {
                    zone.FeeRules.Add(new DeliveryFeeRule(zone.Id, r.MinOrderAmount, r.MaxOrderAmount, r.Fee));
                }
            }

            db.DeliveryZones.Add(zone);
            audit.Record("CreateDeliveryZone", "DeliveryZone", zone.Id.ToString(), $"Created zone {zone.Name} ({zone.State})");
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/admin/delivery-zones/{zone.Id}", MapAdminDeliveryZone(zone));
        });

        fin.MapPut("/delivery-zones/{id:guid}", async (
            Guid id,
            [FromBody] DeliveryZoneInput input,
            IAuditService audit,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var zone = await db.DeliveryZones.Include(z => z.FeeRules).FirstOrDefaultAsync(z => z.Id == id, ct)
                ?? throw DomainException.NotFound("Delivery zone not found.");

            zone.Update(
                input.Name,
                input.State,
                input.Cities,
                input.BaseFee,
                input.FreeDeliveryThreshold,
                input.EstimatedMinDays,
                input.EstimatedMaxDays,
                input.IsActive);

            foreach (var oldRule in zone.FeeRules.ToList())
            {
                db.DeliveryFeeRules.Remove(oldRule);
            }
            zone.FeeRules.Clear();

            if (input.FeeRules is not null)
            {
                foreach (var r in input.FeeRules)
                {
                    var rule = new DeliveryFeeRule(zone.Id, r.MinOrderAmount, r.MaxOrderAmount, r.Fee);
                    zone.FeeRules.Add(rule);
                    db.DeliveryFeeRules.Add(rule);
                }
            }

            audit.Record("UpdateDeliveryZone", "DeliveryZone", zone.Id.ToString(), $"Updated zone {zone.Name}");
            await db.SaveChangesAsync(ct);
            return Results.Ok(MapAdminDeliveryZone(zone));
        });

        fin.MapGet("/analytics/summary", async (
            string? from,
            string? to,
            OrderAndAdminService adminService,
            CancellationToken ct) =>
        {
            DateOnly? fromDate = DateOnly.TryParse(from, out var f) ? f : null;
            DateOnly? toDate = DateOnly.TryParse(to, out var t) ? t : null;
            return Results.Ok(await adminService.GetSalesSummaryAsync(fromDate, toDate, ct));
        });

        fin.MapGet("/audit-logs", async (
            int? page,
            int? pageSize,
            string? entityType,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 30, 1, 100);
            var query = db.AuditLogs.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(entityType))
            {
                query = query.Where(a => a.EntityType == entityType.Trim());
            }

            var total = await query.CountAsync(ct);
            var list = await query
                .OrderByDescending(a => a.CreatedAt)
                .Skip((p - 1) * ps)
                .Take(ps)
                .Select(a => new AuditLogDto(a.Id.ToString(), a.Actor, a.Action, a.EntityType, a.EntityId, a.Details, a.IpAddress, a.CreatedAt))
                .ToListAsync(ct);

            return Results.Ok(PagedResult<AuditLogDto>.Create(list, p, ps, total));
        });
    }

    private static AdminDeliveryZoneDto MapAdminDeliveryZone(DeliveryZone z) =>
        new(
            z.Id.ToString(),
            z.Name,
            z.State,
            z.GetCities(),
            z.BaseFee,
            z.FreeDeliveryThreshold,
            z.EstimatedMinDays,
            z.EstimatedMaxDays,
            z.IsActive,
            z.FeeRules.OrderBy(r => r.MinOrderAmount).Select(r => new DeliveryFeeRuleInput(r.MinOrderAmount, r.MaxOrderAmount, r.Fee)).ToList());

    private static string? DetectSafeImageExtension(byte[] header, int length)
    {
        if (length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
        {
            return ".jpg";
        }
        if (length >= 8 &&
            header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47 &&
            header[4] == 0x0D && header[5] == 0x0A && header[6] == 0x1A && header[7] == 0x0A)
        {
            return ".png";
        }
        if (length >= 12 &&
            header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
            header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P')
        {
            return ".webp";
        }
        return null;
    }
}
