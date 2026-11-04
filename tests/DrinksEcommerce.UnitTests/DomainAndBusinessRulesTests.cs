using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Customers;
using DrinksEcommerce.Domain.Delivery;
using DrinksEcommerce.Domain.Enums;
using DrinksEcommerce.Domain.Inventory;
using DrinksEcommerce.Domain.Orders;
using DrinksEcommerce.Domain.Payments;
using DrinksEcommerce.Domain.Products;
using DrinksEcommerce.Infrastructure.Payments.Paystack;
using FluentAssertions;

namespace DrinksEcommerce.UnitTests;

public class DomainAndBusinessRulesTests
{
    [Theory]
    [InlineData(400, 40000)]
    [InlineData(4500.50, 450050)]
    [InlineData(0.01, 1)]
    [InlineData(125000.99, 12500099)]
    public void ToKobo_ConvertsValidNairaAmountsExact(decimal naira, long expectedKobo)
    {
        NigerianValueRules.ToKobo(naira).Should().Be(expectedKobo);
        NigerianValueRules.FromKobo(expectedKobo).Should().Be(naira);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-100)]
    [InlineData(120.555)] // Sub-kobo precision rejected
    public void ToKobo_RejectsInvalidOrNegativeOrSubKoboPrecision(decimal invalidAmount)
    {
        Action act = () => NigerianValueRules.ToKobo(invalidAmount);
        act.Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData("08031234567", "+2348031234567")]
    [InlineData("+2348031234567", "+2348031234567")]
    [InlineData("2349012345678", "+2349012345678")]
    [InlineData("0706 111 2222", "+2347061112222")]
    public void NormalizeNigerianPhone_AcceptsValidNigerianNumbers(string input, string expected)
    {
        NigerianValueRules.TryNormalizeNigerianPhone(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("+14155552671")]
    [InlineData("06031234567")]
    public void NormalizeNigerianPhone_RejectsInvalidNumbers(string invalid)
    {
        NigerianValueRules.TryNormalizeNigerianPhone(invalid, out _).Should().BeFalse();
    }

    [Fact]
    public void ProductVariant_PackValidation_RejectsCartonWithSingleUnit()
    {
        var productId = Guid.NewGuid();
        Action act = () => new ProductVariant(productId, "COKE-CTN", "Carton", PackType.Carton, 1, 500, 4500m);
        act.Should().Throw<DomainException>().WithMessage("*at least 2 units*");

        var validCarton = new ProductVariant(productId, "coke-ctn-12", "Carton (12 x 50cl)", PackType.Carton, 12, 500, 4500m);
        validCarton.Sku.Should().Be("COKE-CTN-12");
        validCarton.UnitsPerPack.Should().Be(12);
    }

    [Fact]
    public void Coupon_Evaluate_AppliesPercentageAndMaxDiscountCapAndMinSubtotal()
    {
        var coupon = new Coupon(
            "welcome10",
            "10% off",
            DiscountType.Percentage,
            10m,
            minOrderAmount: 5000m,
            maxDiscountAmount: 2000m,
            startsAt: DateTimeOffset.UtcNow.AddDays(-1),
            endsAt: DateTimeOffset.UtcNow.AddDays(1),
            usageLimit: 100,
            perCustomerLimit: 2);

        var (belowMinEligible, _, belowMinReason) = coupon.Evaluate(4000m, 0, DateTimeOffset.UtcNow);
        belowMinEligible.Should().BeFalse();
        belowMinReason.Should().Contain("5,000");

        var (eligible10k, disc10k, _) = coupon.Evaluate(10000m, 0, DateTimeOffset.UtcNow);
        eligible10k.Should().BeTrue();
        disc10k.Should().Be(1000m);

        // Capped at 2,000 NGN even on 35,000 NGN order
        var (eligible35k, disc35k, _) = coupon.Evaluate(35000m, 0, DateTimeOffset.UtcNow);
        eligible35k.Should().BeTrue();
        disc35k.Should().Be(2000m);

        // Per-customer limit reached
        var (limitEligible, _, _) = coupon.Evaluate(10000m, 2, DateTimeOffset.UtcNow);
        limitEligible.Should().BeFalse();
    }

    [Fact]
    public void DeliveryZone_CalculatesTieredFeeAndFreeThresholdAndMatchesCity()
    {
        var zone = new DeliveryZone(
            "Lagos Island",
            "Lagos",
            ["Victoria Island", "Lekki", "Ikoyi"],
            baseFee: 2000m,
            freeDeliveryThreshold: 35000m,
            estimatedMinDays: 1,
            estimatedMaxDays: 1);
        zone.FeeRules.Add(new DeliveryFeeRule(zone.Id, 20000m, 34999.99m, 1200m));

        zone.Matches("Lagos", "Lekki Phase 1").Should().BeTrue();
        zone.Matches("Lagos", "Ikeja").Should().BeFalse();
        zone.Matches("Kano", "Kano").Should().BeFalse();

        zone.CalculateFee(10000m).Should().Be((2000m, false));
        zone.CalculateFee(25000m).Should().Be((1200m, false));
        zone.CalculateFee(35000m).Should().Be((0m, true));
    }

    [Fact]
    public void InventoryItem_ReserveCommitReleaseAndAdjustment_PreventsNegativeOrOversoldStock()
    {
        var variantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var inv = new InventoryItem(variantId, initialOnHand: 20, lowStockThreshold: 5);
        var initialToken = inv.RowVersion;

        inv.Available.Should().Be(20);
        inv.Reserve(8, orderId, "customer@test.local");
        inv.Available.Should().Be(12);
        inv.Reserved.Should().Be(8);
        inv.OnHand.Should().Be(20);
        inv.RowVersion.Should().NotEqual(initialToken);

        // Oversell attempt rejected
        Action oversell = () => inv.Reserve(15, Guid.NewGuid(), "other@test.local");
        oversell.Should().Throw<DomainException>().Where(e => e.Code == "insufficient_stock");

        // Manual downward adjustment below reserved stock rejected
        Action badAdjust = () => inv.Adjust(-15, InventoryTransactionType.Damage, "Broken crate", "warehouse@test.local");
        badAdjust.Should().Throw<DomainException>().Where(e => e.Code == "insufficient_stock");

        // Commit reserved sale deducts OnHand and Reserved once
        inv.CommitReservedSale(8, orderId, "PaystackVerifier");
        inv.OnHand.Should().Be(12);
        inv.Reserved.Should().Be(0);
        inv.Available.Should().Be(12);
    }

    [Fact]
    public void StockReservation_CommitAndReleaseAreIdempotent()
    {
        var orderId = Guid.NewGuid();
        var variantId = Guid.NewGuid();
        var res = new StockReservation(orderId, DateTimeOffset.UtcNow.AddMinutes(30), [(variantId, 3)]);

        res.TryMarkCommitted().Should().BeTrue();
        res.TryMarkCommitted().Should().BeFalse(); // Second call returns false so stock is never double-decremented
        res.TryMarkReleased().Should().BeFalse();
    }

    [Fact]
    public void OrderStateMachine_EnforcesValidTransitionsAndRejectsIllegalOnes()
    {
        var order = new Order(
            "DRK-20261025-ABC123",
            Guid.NewGuid(),
            "Chinedu Eze",
            "customer@drinksecommerce.local",
            "+2348031000004",
            DeliveryMethod.Delivery,
            new OrderAddressSnapshot("Chinedu Eze", "+2348031000004", "Lagos", "Lekki", "14 Admiralty Way", null, null),
            null,
            null,
            null,
            9000m,
            0m,
            2000m,
            0m,
            "idem-1",
            DateTimeOffset.UtcNow.AddMinutes(30),
            [new OrderItem(Guid.NewGuid(), Guid.NewGuid(), "Coca-Cola", "Carton", "COKE-CTN", null, 4500m, 2)]);

        // Cannot jump straight from PendingPayment to Processing or Delivered
        Action jumpToProcessing = () => order.TransitionTo(OrderStatus.Processing, "skip payment", "staff");
        jumpToProcessing.Should().Throw<DomainException>().Where(e => e.Code == "invalid_state_transition");

        order.MarkConfirmedByVerifiedPayment("pay_ref_1");
        order.Status.Should().Be(OrderStatus.Confirmed);

        order.TransitionTo(OrderStatus.Processing, "Packing", "warehouse");
        order.TransitionTo(OrderStatus.ReadyForDispatch, "Packed", "warehouse");
        order.TransitionTo(OrderStatus.OutForDelivery, "Dispatched", "rider");
        order.TransitionTo(OrderStatus.Delivered, "Received by customer", "rider");
        order.Status.Should().Be(OrderStatus.Delivered);
        order.DeliveredAt.Should().NotBeNull();
    }

    [Fact]
    public void PaymentAndRefund_EnforcesCumulativeRefundLimitAndPendingBeforeProcessed()
    {
        var orderId = Guid.NewGuid();
        var payment = new Payment(orderId, "DRK-20261025-111", "customer@test.local", 10000m, "pay_ref_100");
        payment.MarkVerifiedSuccess("tx_100", "card", "Approved", DateTimeOffset.UtcNow);
        payment.Status.Should().Be(PaymentStatus.Successful);

        // Request partial refund of 4,000 NGN -> status becomes RefundPending, RefundedAmount still 0 until Processed
        var r1 = payment.BeginRefund(4000m, "Damaged bottle", "admin@test.local");
        payment.ApplyRefundOutcome(r1, RefundStatus.Pending, "rf_1", null);
        payment.Status.Should().Be(PaymentStatus.RefundPending);
        payment.RefundedAmount.Should().Be(0m);

        // Attempt second refund of 7,000 NGN (4,000 + 7,000 > 10,000) -> rejected!
        Action exceed = () => payment.BeginRefund(7000m, "Excessive refund", "admin@test.local");
        exceed.Should().Throw<DomainException>().Where(e => e.Code == "refund_limit_exceeded");

        // Complete r1 -> PartiallyRefunded with 4,000 NGN refunded
        payment.ApplyRefundOutcome(r1, RefundStatus.Processed, "rf_1", null);
        payment.Status.Should().Be(PaymentStatus.PartiallyRefunded);
        payment.RefundedAmount.Should().Be(4000m);
        payment.RefundableAmount.Should().Be(6000m);

        // Complete remaining 6,000 NGN -> Refunded
        var r2 = payment.BeginRefund(6000m, "Remainder refund", "admin@test.local");
        payment.ApplyRefundOutcome(r2, RefundStatus.Processed, "rf_2", null);
        payment.Status.Should().Be(PaymentStatus.Refunded);
        payment.RefundedAmount.Should().Be(10000m);
        payment.RefundableAmount.Should().Be(0m);
    }

    [Fact]
    public void PaystackWebhookSignature_VerifiesValidHmacSha512AndRejectsTamperedPayload()
    {
        var secret = "sk_test_secret_key_for_unit_testing";
        var payload = """{"event":"charge.success","data":{"id":98765,"reference":"pay_drk_123","amount":450000,"currency":"NGN","status":"success"}}""";
        var validSig = PaystackPaymentGateway.ComputeHmacSha512Hex(payload, secret);

        var gw = new PaystackPaymentGateway(
            new HttpClient(),
            new SimulatedPaymentStore(),
            Microsoft.Extensions.Options.Options.Create(new DrinksEcommerce.Application.Common.PaystackOptions { SecretKey = secret }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PaystackPaymentGateway>.Instance);

        gw.VerifyWebhookSignature(payload, validSig).Should().BeTrue();
        gw.VerifyWebhookSignature(payload, validSig.ToUpperInvariant()).Should().BeTrue();
        gw.VerifyWebhookSignature(payload + " ", validSig).Should().BeFalse();
        gw.VerifyWebhookSignature(payload, "deadbeef").Should().BeFalse();

        var parsed1 = gw.ParseWebhookEvent(payload);
        var parsed2 = gw.ParseWebhookEvent(payload);
        parsed1.EventType.Should().Be("charge.success");
        parsed1.Reference.Should().Be("pay_drk_123");
        parsed1.AmountKobo.Should().Be(450000);
        parsed1.DedupKey.Should().Be(parsed2.DedupKey);
    }
}
