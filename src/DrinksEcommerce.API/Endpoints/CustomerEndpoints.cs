using System.Text;
using System.Text.Json;
using DrinksEcommerce.API.Services;
using DrinksEcommerce.Application.Checkout;
using DrinksEcommerce.Application.Common;
using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Application.Contracts;
using DrinksEcommerce.Application.Delivery;
using DrinksEcommerce.Application.Orders;
using DrinksEcommerce.Application.Payments;
using DrinksEcommerce.Application.Products;
using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Enums;
using DrinksEcommerce.Infrastructure.Payments.Paystack;
using DrinksEcommerce.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DrinksEcommerce.API.Endpoints;

public static class CustomerEndpoints
{
    public static void MapCustomerEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1");

        // 1. Auth & Account
        var auth = api.MapGroup("/auth").RequireRateLimiting("auth");

        auth.MapPost("/register", async (
            [FromBody] RegisterRequest request,
            AuthAndAccountService authService,
            CancellationToken ct) =>
        {
            var res = await authService.RegisterAsync(request, ct);
            return Results.Created("/api/v1/account/profile", res);
        });

        auth.MapPost("/login", async (
            [FromBody] LoginRequest request,
            AuthAndAccountService authService,
            CancellationToken ct) =>
        {
            var res = await authService.LoginAsync(request, ct);
            return Results.Ok(res);
        });

        auth.MapPost("/refresh", async (
            [FromBody] RefreshRequest request,
            AuthAndAccountService authService,
            CancellationToken ct) =>
        {
            var res = await authService.RefreshAsync(request.RefreshToken, ct);
            return Results.Ok(res);
        });

        auth.MapPost("/logout", async (
            [FromBody] RefreshRequest? request,
            ICurrentUserService currentUser,
            AuthAndAccountService authService,
            CancellationToken ct) =>
        {
            await authService.LogoutAsync(request?.RefreshToken, currentUser.UserId, ct);
            return Results.NoContent();
        });

        auth.MapPost("/forgot-password", async (
            [FromBody] ForgotPasswordRequest request,
            AuthAndAccountService authService,
            CancellationToken ct) =>
        {
            await authService.ForgotPasswordAsync(request.Email, ct);
            return Results.Accepted();
        });

        auth.MapPost("/reset-password", async (
            [FromBody] ResetPasswordRequest request,
            AuthAndAccountService authService) =>
        {
            await authService.ResetPasswordAsync(request);
            return Results.NoContent();
        });

        auth.MapPost("/confirm-email", async (
            [FromBody] ConfirmEmailRequest request,
            AuthAndAccountService authService) =>
        {
            await authService.ConfirmEmailAsync(request);
            return Results.NoContent();
        });

        auth.MapPost("/resend-confirmation", async (
            ICurrentUserService currentUser,
            AuthAndAccountService authService,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            await authService.ResendConfirmationAsync(uid, ct);
            return Results.Accepted();
        }).RequireAuthorization();

        var account = api.MapGroup("/account").RequireAuthorization();

        account.MapGet("/profile", async (ICurrentUserService currentUser, AuthAndAccountService authService) =>
        {
            var uid = RequireUserId(currentUser);
            return Results.Ok(await authService.GetProfileAsync(uid));
        });

        account.MapPut("/profile", async (
            [FromBody] UpdateProfileRequest request,
            ICurrentUserService currentUser,
            AuthAndAccountService authService) =>
        {
            var uid = RequireUserId(currentUser);
            return Results.Ok(await authService.UpdateProfileAsync(uid, request));
        });

        account.MapPost("/change-password", async (
            [FromBody] ChangePasswordRequest request,
            ICurrentUserService currentUser,
            AuthAndAccountService authService) =>
        {
            var uid = RequireUserId(currentUser);
            await authService.ChangePasswordAsync(uid, request);
            return Results.NoContent();
        });

        // 2. Addresses
        account.MapGet("/addresses", async (ICurrentUserService currentUser, AuthAndAccountService authService, CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            return Results.Ok(await authService.GetAddressesAsync(uid, ct));
        });

        account.MapPost("/addresses", async (
            [FromBody] AddressInput input,
            ICurrentUserService currentUser,
            AuthAndAccountService authService,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            var created = await authService.CreateAddressAsync(uid, input, ct);
            return Results.Created($"/api/v1/account/addresses/{created.Id}", created);
        });

        account.MapPut("/addresses/{id:guid}", async (
            Guid id,
            [FromBody] AddressInput input,
            ICurrentUserService currentUser,
            AuthAndAccountService authService,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            return Results.Ok(await authService.UpdateAddressAsync(uid, id, input, ct));
        });

        account.MapDelete("/addresses/{id:guid}", async (
            Guid id,
            ICurrentUserService currentUser,
            AuthAndAccountService authService,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            await authService.DeleteAddressAsync(uid, id, ct);
            return Results.NoContent();
        });

        // 3. Public Catalog
        api.MapGet("/products", async (
            string? search,
            Guid? categoryId,
            string? categorySlug,
            string? brand,
            decimal? minPrice,
            decimal? maxPrice,
            bool? inStockOnly,
            bool? featured,
            string? sort,
            int? page,
            int? pageSize,
            CatalogService catalog,
            CancellationToken ct) =>
        {
            var res = await catalog.GetProductsAsync(
                search,
                categoryId,
                categorySlug,
                brand,
                minPrice,
                maxPrice,
                inStockOnly,
                featured,
                sort,
                page ?? 1,
                pageSize ?? 12,
                ct);
            return Results.Ok(res);
        });

        api.MapGet("/products/{idOrSlug}", async (string idOrSlug, CatalogService catalog, CancellationToken ct) =>
            Results.Ok(await catalog.GetProductByIdOrSlugAsync(idOrSlug, ct)));

        api.MapGet("/categories", async (CatalogService catalog, CancellationToken ct) =>
            Results.Ok(await catalog.GetCategoriesAsync(ct)));

        api.MapGet("/categories/{idOrSlug}", async (string idOrSlug, CatalogService catalog, CancellationToken ct) =>
            Results.Ok(await catalog.GetCategoryByIdOrSlugAsync(idOrSlug, ct)));

        api.MapGet("/categories/{idOrSlug}/products", async (
            string idOrSlug,
            string? sort,
            int? page,
            int? pageSize,
            string? search,
            CatalogService catalog,
            CancellationToken ct) =>
        {
            var cat = await catalog.GetCategoryByIdOrSlugAsync(idOrSlug, ct);
            var res = await catalog.GetProductsAsync(
                search,
                Guid.Parse(cat.Id),
                null,
                null,
                null,
                null,
                null,
                null,
                sort,
                page ?? 1,
                pageSize ?? 12,
                ct);
            return Results.Ok(res);
        });

        api.MapGet("/brands", async (CatalogService catalog, CancellationToken ct) =>
            Results.Ok(await catalog.GetBrandsAsync(ct)));

        api.MapGet("/promotions/active", async (CatalogService catalog, CancellationToken ct) =>
            Results.Ok(await catalog.GetActivePromotionsAsync(ct)));

        // 4. Cart
        var cart = api.MapGroup("/cart").RequireAuthorization();

        cart.MapGet("", async (ICurrentUserService currentUser, CartAndCheckoutService cartService, CancellationToken ct) =>
            Results.Ok(await cartService.GetCartAsync(RequireUserId(currentUser), ct)));

        cart.MapPost("/items", async (
            [FromBody] AddCartItemRequest request,
            ICurrentUserService currentUser,
            CartAndCheckoutService cartService,
            CancellationToken ct) =>
        {
            var res = await cartService.AddItemAsync(RequireUserId(currentUser), request.VariantId, request.Quantity, ct);
            return Results.Ok(res);
        });

        cart.MapPut("/items/{itemId:guid}", async (
            Guid itemId,
            [FromBody] UpdateCartItemRequest request,
            ICurrentUserService currentUser,
            CartAndCheckoutService cartService,
            CancellationToken ct) =>
        {
            var res = await cartService.UpdateItemAsync(RequireUserId(currentUser), itemId, request.Quantity, ct);
            return Results.Ok(res);
        });

        cart.MapDelete("/items/{itemId:guid}", async (
            Guid itemId,
            ICurrentUserService currentUser,
            CartAndCheckoutService cartService,
            CancellationToken ct) =>
        {
            var res = await cartService.RemoveItemAsync(RequireUserId(currentUser), itemId, ct);
            return Results.Ok(res);
        });

        cart.MapDelete("", async (ICurrentUserService currentUser, CartAndCheckoutService cartService, CancellationToken ct) =>
        {
            await cartService.ClearCartAsync(RequireUserId(currentUser), ct);
            return Results.NoContent();
        });

        cart.MapPost("/coupon", async (
            [FromBody] ApplyCouponRequest request,
            ICurrentUserService currentUser,
            CartAndCheckoutService cartService,
            CancellationToken ct) =>
        {
            var res = await cartService.ApplyCouponAsync(RequireUserId(currentUser), request.Code, ct);
            return Results.Ok(res);
        });

        cart.MapDelete("/coupon", async (ICurrentUserService currentUser, CartAndCheckoutService cartService, CancellationToken ct) =>
            Results.Ok(await cartService.RemoveCouponAsync(RequireUserId(currentUser), ct)));

        cart.MapPost("/merge", async (
            [FromBody] MergeCartRequest request,
            ICurrentUserService currentUser,
            CartAndCheckoutService cartService,
            CancellationToken ct) =>
        {
            var res = await cartService.MergeGuestCartAsync(RequireUserId(currentUser), request.Items, ct);
            return Results.Ok(res);
        });

        // 5. Delivery
        var delivery = api.MapGroup("/delivery");

        delivery.MapGet("/zones", async (CartAndCheckoutService cartService, CancellationToken ct) =>
            Results.Ok(await cartService.GetActiveDeliveryZonesAsync(ct)));

        delivery.MapGet("/estimate", async (
            string? state,
            string? city,
            decimal? subtotal,
            CartAndCheckoutService cartService,
            CancellationToken ct) =>
        {
            var res = await cartService.EstimateDeliveryAsync(state, city, subtotal ?? 0m, ct);
            return Results.Ok(res);
        });

        delivery.MapGet("/pickup-locations", async (CartAndCheckoutService cartService, CancellationToken ct) =>
            Results.Ok(await cartService.GetPickupLocationsAsync(ct)));

        delivery.MapGet("/states", () => Results.Ok(NigerianStates.All));

        // 6. Checkout
        var checkout = api.MapGroup("/checkout").RequireAuthorization();

        checkout.MapPost("/preview", async (
            [FromBody] CheckoutRequest request,
            ICurrentUserService currentUser,
            CartAndCheckoutService cartService,
            CancellationToken ct) =>
        {
            var res = await cartService.PreviewCheckoutAsync(RequireUserId(currentUser), request, ct);
            return Results.Ok(res);
        });

        checkout.MapPost("", async (
            HttpContext httpContext,
            [FromBody] CheckoutRequest request,
            ICurrentUserService currentUser,
            AuthAndAccountService authService,
            CartAndCheckoutService cartService,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            var idempotencyKey = httpContext.Request.Headers["Idempotency-Key"].FirstOrDefault() ?? string.Empty;
            var profile = await authService.GetProfileAsync(uid);
            var (result, createdNew) = await cartService.SubmitCheckoutAsync(
                uid,
                $"{profile.FirstName} {profile.LastName}".Trim(),
                profile.Email,
                profile.PhoneNumber,
                request,
                idempotencyKey,
                ct);

            return createdNew
                ? Results.Created($"/api/v1/orders/{result.OrderId}", result)
                : Results.Ok(result);
        });

        // 7. Payments
        var payments = api.MapGroup("/payments");

        payments.MapPost("/initialize", async (
            [FromBody] InitializePaymentForOrderRequest request,
            ICurrentUserService currentUser,
            CartAndCheckoutService cartService,
            CancellationToken ct) =>
        {
            var res = await cartService.InitializePaymentForPendingOrderAsync(
                RequireUserId(currentUser),
                request.OrderId,
                request.Client,
                ct);
            return Results.Ok(res);
        }).RequireAuthorization();

        payments.MapGet("/{reference}/verify", async (
            string reference,
            ICurrentUserService currentUser,
            PaymentOrchestratorService orchestrator,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            var isAdmin = currentUser.IsInRole(Roles.Administrator);
            var res = await orchestrator.VerifyAndApplyPaymentAsync(uid, isAdmin, reference, ct);
            return Results.Ok(res);
        }).RequireAuthorization();

        payments.MapGet("/{reference}", async (
            string reference,
            ICurrentUserService currentUser,
            PaymentOrchestratorService orchestrator,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            var isAdmin = currentUser.IsInRole(Roles.Administrator);
            var res = await orchestrator.GetPaymentStatusAsync(uid, isAdmin, reference, ct);
            return Results.Ok(res);
        }).RequireAuthorization();

        payments.MapPost("/paystack/webhook", async (
            HttpContext httpContext,
            PaymentOrchestratorService orchestrator,
            CancellationToken ct) =>
        {
            httpContext.Request.EnableBuffering();
            using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var rawPayload = await reader.ReadToEndAsync(ct);
            httpContext.Request.Body.Position = 0;

            var signature = httpContext.Request.Headers["x-paystack-signature"].FirstOrDefault();
            var (accepted, message) = await orchestrator.ReceivePaystackWebhookAsync(rawPayload, signature, ct);

            if (!accepted)
            {
                return Results.Json(new { status = 401, title = "Unauthorized", detail = message, code = "invalid_webhook_signature" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Ok(new { status = "acknowledged", detail = message });
        });

        // 8. Customer Orders & Tracking
        var orders = api.MapGroup("/orders").RequireAuthorization();

        orders.MapGet("", async (
            OrderStatus? status,
            int? page,
            int? pageSize,
            ICurrentUserService currentUser,
            OrderAndAdminService orderService,
            CancellationToken ct) =>
        {
            var res = await orderService.GetMyOrdersAsync(RequireUserId(currentUser), status, page ?? 1, pageSize ?? 12, ct);
            return Results.Ok(res);
        });

        orders.MapGet("/{id:guid}", async (
            Guid id,
            ICurrentUserService currentUser,
            OrderAndAdminService orderService,
            CancellationToken ct) =>
        {
            var res = await orderService.GetMyOrderDetailAsync(RequireUserId(currentUser), id, ct);
            return Results.Ok(res);
        });

        orders.MapPost("/{id:guid}/cancel", async (
            Guid id,
            [FromBody] CancelOrderRequest? request,
            ICurrentUserService currentUser,
            OrderAndAdminService orderService,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            var res = await orderService.CancelMyOrderAsync(uid, currentUser.Email ?? uid.ToString(), id, request?.Reason, ct);
            return Results.Ok(res);
        });

        orders.MapGet("/{id:guid}/tracking", async (
            Guid id,
            ICurrentUserService currentUser,
            OrderAndAdminService orderService,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            var isStaff = Roles.Staff.Any(currentUser.IsInRole);
            var res = await orderService.GetOrderTrackingAsync(uid, isStaff, id, ct);
            return Results.Ok(res);
        });

        // 9. Notifications
        var notif = api.MapGroup("/notifications").RequireAuthorization();

        notif.MapGet("", async (
            int? page,
            int? pageSize,
            bool? unreadOnly,
            ICurrentUserService currentUser,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 20, 1, 100);

            var query = db.Notifications.AsNoTracking().Where(n => n.UserId == uid);
            if (unreadOnly == true)
            {
                query = query.Where(n => !n.IsRead);
            }

            var total = await query.CountAsync(ct);
            var items = await query
                .OrderByDescending(n => n.CreatedAt)
                .Skip((p - 1) * ps)
                .Take(ps)
                .Select(n => new NotificationDto(n.Id.ToString(), n.Type, n.Title, n.Message, n.OrderId == null ? null : n.OrderId.ToString(), n.IsRead, n.CreatedAt))
                .ToListAsync(ct);

            return Results.Ok(PagedResult<NotificationDto>.Create(items, p, ps, total));
        });

        notif.MapGet("/unread-count", async (
            ICurrentUserService currentUser,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            var count = await db.Notifications.CountAsync(n => n.UserId == uid && !n.IsRead, ct);
            return Results.Ok(new { count });
        });

        notif.MapPost("/{id:guid}/read", async (
            Guid id,
            ICurrentUserService currentUser,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            var entity = await db.Notifications.FirstOrDefaultAsync(n => n.Id == id && n.UserId == uid, ct);
            if (entity is not null)
            {
                entity.MarkRead();
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });

        notif.MapPost("/read-all", async (
            ICurrentUserService currentUser,
            ApplicationDbContext db,
            CancellationToken ct) =>
        {
            var uid = RequireUserId(currentUser);
            var unread = await db.Notifications.Where(n => n.UserId == uid && !n.IsRead).ToListAsync(ct);
            foreach (var n in unread)
            {
                n.MarkRead();
            }
            if (unread.Count > 0)
            {
                await db.SaveChangesAsync(ct);
            }
            return Results.NoContent();
        });
    }

    public static void MapDevPaystackSimulatorEndpoints(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment() && !string.Equals(app.Configuration["Payments:Provider"], "Simulated", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        app.MapGet("/dev/paystack/checkout", (
            string reference,
            string? callbackUrl,
            SimulatedPaymentStore simStore) =>
        {
            var state = simStore.Get(reference);
            var amountNgn = state is not null ? NigerianValueRules.FromKobo(state.AmountKobo) : 0m;
            var cb = callbackUrl ?? state?.CallbackUrl ?? $"http://localhost:5173/payment/verify?reference={Uri.EscapeDataString(reference)}";

            var html = $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>Paystack Test Checkout Simulator — DrinksEcommerce</title>
              <style>
                body { font-family: system-ui, -apple-system, sans-serif; background: #0f172a; color: #f8fafc; display: flex; justify-content: center; align-items: center; min-height: 100vh; margin: 0; padding: 1rem; }
                .card { background: #1e293b; border: 1px solid #334155; border-radius: 16px; max-width: 440px; width: 100%; padding: 2rem; box-shadow: 0 20px 40px rgba(0,0,0,0.4); }
                .badge { display: inline-block; background: #0f766e; color: #ccfbf1; font-size: 0.75rem; font-weight: 700; padding: 0.25rem 0.65rem; border-radius: 999px; text-transform: uppercase; letter-spacing: 0.05em; }
                h1 { font-size: 1.35rem; margin: 0.75rem 0 0.25rem; }
                .amount { font-size: 2rem; font-weight: 800; color: #38bdf8; margin: 1rem 0; }
                .meta { font-size: 0.875rem; color: #94a3b8; margin-bottom: 1.5rem; word-break: break-all; }
                .btn { display: block; width: 100%; padding: 0.85rem 1rem; border-radius: 10px; border: none; font-weight: 700; font-size: 0.95rem; cursor: pointer; margin-bottom: 0.75rem; text-align: center; }
                .btn-success { background: #10b981; color: #052e16; }
                .btn-fail { background: #ef4444; color: #fff; }
                .btn-abandon { background: #475569; color: #e2e8f0; }
              </style>
            </head>
            <body>
              <div class="card">
                <span class="badge">Paystack Test Mode Simulator</span>
                <h1>DrinksEcommerce Nigeria</h1>
                <div class="amount">₦{{amountNgn:N2}}</div>
                <div class="meta">
                  <div><strong>Reference:</strong> {{reference}}</div>
                  <div><strong>Customer:</strong> {{state?.CustomerEmail ?? "customer"}}</div>
                </div>
                <form method="post" action="/dev/paystack/complete">
                  <input type="hidden" name="reference" value="{{reference}}" />
                  <input type="hidden" name="callbackUrl" value="{{cb}}" />
                  <button class="btn btn-success" type="submit" name="outcome" value="success">Simulate Successful Card Payment (NGN)</button>
                  <button class="btn btn-fail" type="submit" name="outcome" value="failed">Simulate Declined Payment</button>
                  <button class="btn btn-abandon" type="submit" name="outcome" value="abandoned">Cancel &amp; Abandon Checkout</button>
                </form>
              </div>
            </body>
            </html>
            """;

            return Results.Content(html, "text/html");
        });

        app.MapPost("/dev/paystack/complete", async (
            HttpContext httpContext,
            SimulatedPaymentStore simStore,
            PaymentOrchestratorService orchestrator,
            IOptions<PaystackOptions> paystackOptions,
            CancellationToken ct) =>
        {
            var form = await httpContext.Request.ReadFormAsync(ct);
            var reference = form["reference"].ToString();
            var outcome = form["outcome"].ToString().ToLowerInvariant();
            var callbackUrl = form["callbackUrl"].ToString();

            var state = simStore.Get(reference);
            if (state is not null)
            {
                state.ProviderStatus = outcome switch
                {
                    "success" => "success",
                    "failed" => "failed",
                    _ => "abandoned"
                };
                state.GatewayResponse = outcome switch
                {
                    "success" => "Approved by simulated issuer",
                    "failed" => "Declined by simulated issuer",
                    _ => "Customer abandoned checkout"
                };
                if (outcome == "success")
                {
                    state.PaidAtUtc = DateTimeOffset.UtcNow;
                }

                // Dispatch a signed Paystack webhook event to our own orchestrator
                var eventType = outcome == "success" ? "charge.success" : "charge.failed";
                var webhookPayload = JsonSerializer.Serialize(new
                {
                    @event = eventType,
                    data = new
                    {
                        id = Random.Shared.Next(100000, 999999),
                        reference = state.Reference,
                        amount = state.AmountKobo,
                        currency = state.Currency,
                        status = state.ProviderStatus,
                        channel = "card",
                        gateway_response = state.GatewayResponse,
                        paid_at = state.PaidAtUtc?.ToString("O"),
                        metadata = new
                        {
                            order_id = state.OrderId.ToString(),
                            order_number = state.OrderNumber
                        }
                    }
                });

                var sig = PaystackPaymentGateway.ComputeHmacSha512Hex(webhookPayload, paystackOptions.Value.SecretKey);
                await orchestrator.ReceivePaystackWebhookAsync(webhookPayload, sig, ct);
            }

            var target = string.IsNullOrWhiteSpace(callbackUrl)
                ? $"http://localhost:5173/payment/verify?reference={Uri.EscapeDataString(reference)}&trxref={Uri.EscapeDataString(reference)}"
                : callbackUrl;

            return Results.Redirect(target);
        });
    }

    public static Guid RequireUserId(ICurrentUserService currentUser) =>
        currentUser.UserId ?? throw new DomainException("Authentication is required.", "unauthorized", 401);
}
