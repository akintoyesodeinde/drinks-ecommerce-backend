using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DrinksEcommerce.Application.Contracts;
using DrinksEcommerce.Domain.Enums;
using DrinksEcommerce.Infrastructure.Payments.Paystack;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DrinksEcommerce.IntegrationTests;

public sealed class DrinksApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"DrinksEcommerceIntegrationTest_{Guid.NewGuid():N}";
    public const string TestWebhookSecret = "sk_test_simulated_drinksecommerce_key_do_not_use_in_prod";

    private string SqlConnectionString =>
        Environment.GetEnvironmentVariable("TEST_CONNECTION_STRING")
        ?? $"Server=(localdb)\\mssqllocaldb;Database={_dbName};Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True";

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        var provider = Environment.GetEnvironmentVariable("TEST_DATABASE_PROVIDER") ?? "SqlServer";
        builder.UseSetting("Database:Provider", provider);
        builder.UseSetting("Database:InMemoryName", _dbName);
        builder.UseSetting("ConnectionStrings:DefaultConnection", SqlConnectionString);
        builder.UseSetting("BackgroundJobs:Disabled", "true");
        builder.UseSetting("Payments:Provider", "Simulated");
        builder.UseSetting("Payments:SecretKey", TestWebhookSecret);
        builder.UseSetting("Payments:VerifyBeforeWebhookCommit", "false");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = provider,
                ["Database:InMemoryName"] = _dbName,
                ["ConnectionStrings:DefaultConnection"] = SqlConnectionString,
                ["BackgroundJobs:Disabled"] = "true",
                ["Payments:Provider"] = "Simulated",
                ["Payments:SecretKey"] = TestWebhookSecret,
                ["Payments:VerifyBeforeWebhookCommit"] = "false"
            });
        });
    }

    public override async ValueTask DisposeAsync()
    {
        try
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DrinksEcommerce.Persistence.ApplicationDbContext>();
            await db.Database.EnsureDeletedAsync();
        }
        catch
        {
            // Ignore cleanup errors on disposal
        }

        await base.DisposeAsync();
    }
}

public class EndToEndWorkflowIntegrationTests : IClassFixture<DrinksApiFactory>
{
    private readonly DrinksApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public EndToEndWorkflowIntegrationTests(DrinksApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CompleteCustomerPurchaseToDeliveryAndRefundWorkflow_WorksEndToEnd()
    {
        using var client = _factory.CreateClient();

        // 1. Health & Catalog browsing (alcohol hidden by default)
        var healthResp = await client.GetAsync("/health/live");
        healthResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var catalog = await client.GetFromJsonAsync<PagedResponse<ProductSummaryDto>>("/api/v1/products?pageSize=50", JsonOpts);
        catalog.Should().NotBeNull();
        catalog!.Items.Should().NotBeEmpty();
        catalog.Items.Should().OnlyContain(p => !p.IsAlcoholic);

        var cokeSummary = catalog.Items.First(p => p.Name.Contains("Coca-Cola"));
        var productDetail = await client.GetFromJsonAsync<ProductDetailDto>($"/api/v1/products/{cokeSummary.Slug}", JsonOpts);
        productDetail.Should().NotBeNull();
        var cartonVariant = productDetail!.Variants.First(v => v.PackType == PackType.Carton);
        var initialAvailable = cartonVariant.AvailableQuantity;

        // 2. Customer login & token refresh rotation
        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("customer@drinksecommerce.local", "Customer#12345"));
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var customerAuth = await loginResp.Content.ReadFromJsonAsync<AuthResponse>(JsonOpts);
        customerAuth.Should().NotBeNull();

        var refreshResp = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(customerAuth!.RefreshToken));
        refreshResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotatedAuth = await refreshResp.Content.ReadFromJsonAsync<AuthResponse>(JsonOpts);

        // Reusing old refresh token must fail with 401
        var reuseResp = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(customerAuth.RefreshToken));
        reuseResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Re-login after family revocation test
        var freshLogin = await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("customer@drinksecommerce.local", "Customer#12345")))
            .Content.ReadFromJsonAsync<AuthResponse>(JsonOpts);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", freshLogin!.AccessToken);

        // 3. Add to cart & apply coupon WELCOME10
        var addCartResp = await client.PostAsJsonAsync("/api/v1/cart/items", new AddCartItemRequest(Guid.Parse(cartonVariant.Id), 2));
        addCartResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var cart = await addCartResp.Content.ReadFromJsonAsync<CartDto>(JsonOpts);
        cart!.Subtotal.Should().Be(cartonVariant.Price * 2);

        var couponResp = await client.PostAsJsonAsync("/api/v1/cart/coupon", new ApplyCouponRequest("WELCOME10"));
        couponResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var discountedCart = await couponResp.Content.ReadFromJsonAsync<CartDto>(JsonOpts);
        discountedCart!.Discount.Should().Be(900m); // 10% of 9,000 NGN = 900 NGN

        // 4. Delivery zone check: Kano is unserviceable, Lagos Lekki is serviceable
        var unserviceableEst = await client.GetFromJsonAsync<DeliveryEstimateDto>("/api/v1/delivery/estimate?state=Kano&city=Kano&subtotal=8100", JsonOpts);
        unserviceableEst!.Serviceable.Should().BeFalse();

        var lagosEst = await client.GetFromJsonAsync<DeliveryEstimateDto>("/api/v1/delivery/estimate?state=Lagos&city=Lekki&subtotal=8100", JsonOpts);
        lagosEst!.Serviceable.Should().BeTrue();
        lagosEst.Fee.Should().Be(2000m);

        // 5. Checkout with Idempotency-Key (and verify duplicate submission returns same order)
        var addresses = await client.GetFromJsonAsync<List<AddressDto>>("/api/v1/account/addresses", JsonOpts);
        var addressId = Guid.Parse(addresses!.First().Id);

        var checkoutReq = new CheckoutRequest(
            DeliveryMethod.Delivery,
            addressId,
            null,
            false,
            null,
            "Leave with security at gate",
            null,
            "web");

        var idempotencyKey = Guid.NewGuid().ToString("N");
        using var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout")
        {
            Content = JsonContent.Create(checkoutReq)
        };
        req1.Headers.Add("Idempotency-Key", idempotencyKey);
        var checkoutResp1 = await client.SendAsync(req1);
        checkoutResp1.StatusCode.Should().Be(HttpStatusCode.Created);
        var checkoutResult1 = await checkoutResp1.Content.ReadFromJsonAsync<CheckoutResultDto>(JsonOpts);
        checkoutResult1!.Total.Should().Be(10100m); // 9000 - 900 + 2000

        // Retry with same Idempotency-Key -> returns 200 OK with exact same orderId and reference
        using var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout")
        {
            Content = JsonContent.Create(checkoutReq)
        };
        req2.Headers.Add("Idempotency-Key", idempotencyKey);
        var checkoutResp2 = await client.SendAsync(req2);
        checkoutResp2.StatusCode.Should().Be(HttpStatusCode.OK);
        var checkoutResult2 = await checkoutResp2.Content.ReadFromJsonAsync<CheckoutResultDto>(JsonOpts);
        checkoutResult2!.OrderId.Should().Be(checkoutResult1.OrderId);
        checkoutResult2.PaymentReference.Should().Be(checkoutResult1.PaymentReference);

        // 6. Invalid webhook signature is rejected with 401
        var webhookPayload = JsonSerializer.Serialize(new
        {
            @event = "charge.success",
            data = new
            {
                id = 555111,
                reference = checkoutResult1.PaymentReference,
                amount = 1010000, // 10,100.00 NGN in kobo
                currency = "NGN",
                status = "success",
                channel = "card",
                gateway_response = "Approved",
                metadata = new { order_id = checkoutResult1.OrderId, order_number = checkoutResult1.OrderNumber }
            }
        });

        using var badWebhook = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/paystack/webhook")
        {
            Content = new StringContent(webhookPayload, Encoding.UTF8, "application/json")
        };
        badWebhook.Headers.Add("x-paystack-signature", "invalid_signature_hex");
        var badWebhookResp = await client.SendAsync(badWebhook);
        badWebhookResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 7. Valid signed webhook confirms payment & commits stock; duplicate webhook does not double-decrement stock
        var validSig = PaystackPaymentGateway.ComputeHmacSha512Hex(webhookPayload, DrinksApiFactory.TestWebhookSecret);
        for (var i = 0; i < 2; i++)
        {
            using var goodWebhook = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/paystack/webhook")
            {
                Content = new StringContent(webhookPayload, Encoding.UTF8, "application/json")
            };
            goodWebhook.Headers.Add("x-paystack-signature", validSig);
            var whResp = await client.SendAsync(goodWebhook);
            whResp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Verify browser callback after webhook still returns Confirmed and stock was decremented only once (by 2)
        var verifyDto = await client.GetFromJsonAsync<PaymentStatusDto>($"/api/v1/payments/{checkoutResult1.PaymentReference}/verify", JsonOpts);
        verifyDto!.Status.Should().Be(PaymentStatus.Successful);
        verifyDto.OrderStatus.Should().Be(OrderStatus.Confirmed);

        var updatedProduct = await client.GetFromJsonAsync<ProductDetailDto>($"/api/v1/products/{cokeSummary.Slug}", JsonOpts);
        var updatedCarton = updatedProduct!.Variants.First(v => v.Id == cartonVariant.Id);
        updatedCarton.AvailableQuantity.Should().Be(initialAvailable - 2);

        // 8. Another customer cannot view this customer's order (ownership check)
        var regOtherResp = await client.PostAsJsonAsync("/api/v1/auth/register",
            new RegisterRequest("Bola", "Adeyemi", $"bola_{Guid.NewGuid():N}@test.local", "08039998877", "OtherPass#123"));
        regOtherResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var otherAuth = await regOtherResp.Content.ReadFromJsonAsync<AuthResponse>(JsonOpts);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherAuth!.AccessToken);
        var forbiddenOrderResp = await client.GetAsync($"/api/v1/orders/{checkoutResult1.OrderId}");
        forbiddenOrderResp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // Customer cannot access admin endpoints
        var customerAdminAttempt = await client.GetAsync("/api/v1/admin/orders");
        customerAdminAttempt.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // 9. Warehouse staff prepares order -> ReadyForDispatch
        var whLogin = await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("warehouse@drinksecommerce.local", "Warehouse#12345")))
            .Content.ReadFromJsonAsync<AuthResponse>(JsonOpts);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", whLogin!.AccessToken);

        var procResp = await client.PostAsJsonAsync($"/api/v1/admin/orders/{checkoutResult1.OrderId}/status",
            new UpdateOrderStatusRequest(OrderStatus.Processing, "Picking cartons from aisle B"));
        procResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var readyResp = await client.PostAsJsonAsync($"/api/v1/admin/orders/{checkoutResult1.OrderId}/status",
            new UpdateOrderStatusRequest(OrderStatus.ReadyForDispatch, "Packed and sealed at dispatch bay"));
        readyResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 10. Admin assigns delivery rider, rider dispatches and completes delivery
        var adminLogin = await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("admin@drinksecommerce.local", "Admin#12345")))
            .Content.ReadFromJsonAsync<AuthResponse>(JsonOpts);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin!.AccessToken);

        var assignResp = await client.PostAsJsonAsync($"/api/v1/admin/deliveries/{checkoutResult1.OrderId}/assign",
            new AssignDeliveryRequest(null, "Musa Ibrahim", "+2348031000003", DateTimeOffset.UtcNow.AddHours(2)));
        assignResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var riderLogin = await (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest("rider@drinksecommerce.local", "Rider#12345")))
            .Content.ReadFromJsonAsync<AuthResponse>(JsonOpts);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", riderLogin!.AccessToken);

        var dispatchResp = await client.PostAsJsonAsync($"/api/v1/admin/deliveries/{checkoutResult1.OrderId}/dispatch",
            new DispatchDeliveryRequest("En route via Lekki-Epe Expressway"));
        dispatchResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var deliverResp = await client.PostAsJsonAsync($"/api/v1/admin/deliveries/{checkoutResult1.OrderId}/deliver",
            new CompleteDeliveryRequest("Chinedu Eze", "Handed over at gate"));
        deliverResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // 11. Customer tracks completed delivery and views notifications
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", freshLogin.AccessToken);
        var tracking = await client.GetFromJsonAsync<TrackingDto>($"/api/v1/orders/{checkoutResult1.OrderId}/tracking", JsonOpts);
        tracking!.OrderStatus.Should().Be(OrderStatus.Delivered);
        tracking.DeliveryStatus.Should().Be(DeliveryStatus.Delivered);
        tracking.Steps.Should().Contain(s => s.Key == "delivered" && s.Completed);

        var notifs = await client.GetFromJsonAsync<PagedResponse<NotificationDto>>("/api/v1/notifications", JsonOpts);
        notifs!.Items.Should().NotBeEmpty();

        // 12. Admin partial refund & over-refund rejection
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminLogin.AccessToken);
        var overRefundResp = await client.PostAsJsonAsync("/api/v1/admin/refunds",
            new CreateRefundRequest(Guid.Parse(checkoutResult1.OrderId), 50000m, "Too large"));
        overRefundResp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var partialRefundResp = await client.PostAsJsonAsync("/api/v1/admin/refunds",
            new CreateRefundRequest(Guid.Parse(checkoutResult1.OrderId), 2000m, "Courtesy delivery fee refund"));
        partialRefundResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var refundDto = await partialRefundResp.Content.ReadFromJsonAsync<RefundDto>(JsonOpts);
        refundDto!.Status.Should().Be(RefundStatus.Pending);

        var syncedRefund = await (await client.PostAsync($"/api/v1/admin/refunds/{refundDto.Id}/sync", null))
            .Content.ReadFromJsonAsync<RefundDto>(JsonOpts);
        syncedRefund!.Status.Should().Be(RefundStatus.Processed);
    }

    private sealed record PagedResponse<T>(List<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);
}
