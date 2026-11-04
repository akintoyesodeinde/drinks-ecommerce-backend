using DrinksEcommerce.Application.Common;
using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Application.Contracts;
using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Delivery;
using DrinksEcommerce.Domain.Enums;
using DrinksEcommerce.Domain.Inventory;
using DrinksEcommerce.Domain.Orders;
using DrinksEcommerce.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DrinksEcommerce.Application.Payments;

public sealed class PaymentOrchestratorService(
    IApplicationDbContext db,
    IPaymentGateway paymentGateway,
    INotificationService notifications,
    IAuditService audit,
    IOptions<PaystackOptions> paystackOptions,
    ILogger<PaymentOrchestratorService> logger)
{
    public async Task<PaymentStatusDto> GetPaymentStatusAsync(Guid callerUserId, bool isAdmin, string reference, CancellationToken ct = default)
    {
        var found = await FindPaymentAndOrderByReferenceAsync(reference, ct)
            ?? throw DomainException.NotFound("Payment record not found.");
        var (payment, order) = found;

        if (!isAdmin && order.UserId != callerUserId)
        {
            throw DomainException.NotFound("Payment record not found.");
        }

        return MapPaymentStatus(payment, order);
    }

    public async Task<PaymentStatusDto> VerifyAndApplyPaymentAsync(
        Guid? callerUserId,
        bool isSystemOrAdmin,
        string reference,
        CancellationToken ct = default)
    {
        var found = await FindPaymentAndOrderByReferenceAsync(reference, ct)
            ?? throw DomainException.NotFound("Payment reference not found.");

        var (payment, order) = found;
        if (!isSystemOrAdmin && order.UserId != callerUserId)
        {
            throw DomainException.NotFound("Payment reference not found.");
        }

        // Already verified & committed -> idempotent return
        if (payment.Status is PaymentStatus.Successful or PaymentStatus.RefundPending or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded)
        {
            return MapPaymentStatus(payment, order);
        }

        var verifyResult = await paymentGateway.VerifyPaymentAsync(reference, ct);
        if (!verifyResult.Succeeded)
        {
            payment.RecordVerificationFailure();
            if (payment.VerificationFailureCount >= 3)
            {
                await EnsureReconciliationRecordAsync(
                    ReconciliationType.RepeatedVerificationFailure,
                    payment.Reference,
                    order.Id,
                    order.OrderNumber,
                    payment.Amount,
                    null,
                    payment.Currency,
                    $"Payment verification failed {payment.VerificationFailureCount} times: {verifyResult.ErrorMessage}",
                    ct);
            }
            await db.SaveChangesAsync(ct);
            return MapPaymentStatus(payment, order, verifyResult.ErrorMessage ?? "Unable to confirm payment status from provider yet.");
        }

        await ApplyVerifiedGatewayOutcomeAsync(payment, order, verifyResult, "PaystackVerifyEndpoint", ct);
        return MapPaymentStatus(payment, order);
    }

    public async Task<(bool Accepted, string Message)> ReceivePaystackWebhookAsync(
        string rawPayload,
        string? signatureHeader,
        CancellationToken ct = default)
    {
        if (!paymentGateway.VerifyWebhookSignature(rawPayload, signatureHeader))
        {
            logger.LogWarning("Rejected Paystack webhook with invalid HMAC-SHA512 signature.");
            return (false, "Invalid webhook signature.");
        }

        var parsed = paymentGateway.ParseWebhookEvent(rawPayload);

        // Deduplicate by DedupKey
        var existingEvent = await db.PaymentWebhookEvents
            .FirstOrDefaultAsync(e => e.DedupKey == parsed.DedupKey, ct);

        if (existingEvent is not null)
        {
            if (!existingEvent.IsProcessed)
            {
                await ProcessStoredWebhookEventAsync(existingEvent.Id, ct);
            }
            return (true, "Duplicate webhook event acknowledged.");
        }

        var webhookEvent = new PaymentWebhookEvent(parsed.DedupKey, parsed.EventType, parsed.Reference, rawPayload);
        db.PaymentWebhookEvents.Add(webhookEvent);
        await db.SaveChangesAsync(ct);

        // Process immediately and durably (background worker also retries any unprocessed events)
        await ProcessStoredWebhookEventAsync(webhookEvent.Id, ct);
        return (true, "Webhook persisted and processed.");
    }

    public async Task ProcessStoredWebhookEventAsync(Guid webhookEventId, CancellationToken ct = default)
    {
        var ev = await db.PaymentWebhookEvents.FirstOrDefaultAsync(e => e.Id == webhookEventId, ct);
        if (ev is null || ev.IsProcessed)
        {
            return;
        }

        try
        {
            var parsed = paymentGateway.ParseWebhookEvent(ev.RawPayload);
            var eventType = parsed.EventType.ToLowerInvariant();

            if (eventType == "charge.success")
            {
                if (string.IsNullOrWhiteSpace(parsed.Reference))
                {
                    ev.MarkProcessed();
                    await db.SaveChangesAsync(ct);
                    return;
                }

                var found = await FindPaymentAndOrderByReferenceAsync(parsed.Reference, ct);
                if (found is null)
                {
                    await EnsureReconciliationRecordAsync(
                        ReconciliationType.GatewayPaymentWithoutOrder,
                        parsed.Reference,
                        null,
                        null,
                        null,
                        parsed.AmountKobo.HasValue ? NigerianValueRules.FromKobo(parsed.AmountKobo.Value) : null,
                        parsed.Currency,
                        $"Webhook charge.success received for unknown reference '{parsed.Reference}'.",
                        ct);
                    ev.MarkProcessed();
                    await db.SaveChangesAsync(ct);
                    return;
                }

                var (payment, order) = found.Value;

                PaymentVerifyResult verifyOutcome;
                if (paystackOptions.Value.VerifyBeforeWebhookCommit)
                {
                    verifyOutcome = await paymentGateway.VerifyPaymentAsync(parsed.Reference, ct);
                    if (!verifyOutcome.Succeeded)
                    {
                        ev.RecordFailure(verifyOutcome.ErrorMessage ?? "Gateway verification call failed during webhook processing.");
                        await db.SaveChangesAsync(ct);
                        return;
                    }
                }
                else
                {
                    verifyOutcome = new PaymentVerifyResult(
                        true,
                        parsed.Reference,
                        parsed.ProviderStatus ?? "success",
                        parsed.AmountKobo ?? payment.ExpectedAmountKobo,
                        parsed.Currency ?? payment.Currency,
                        parsed.ProviderTransactionId,
                        parsed.Channel,
                        parsed.GatewayResponse,
                        DateTimeOffset.UtcNow,
                        order.Id,
                        null);
                }

                await ApplyVerifiedGatewayOutcomeAsync(payment, order, verifyOutcome, "PaystackWebhook", ct);
            }
            else if (eventType is "charge.failed" or "paymentrequest.failed")
            {
                if (!string.IsNullOrWhiteSpace(parsed.Reference))
                {
                    var found = await FindPaymentAndOrderByReferenceAsync(parsed.Reference, ct);
                    if (found is not null)
                    {
                        var (payment, order) = found.Value;
                        if (payment.MarkFailedOrAbandoned(false, parsed.GatewayResponse ?? "Failed via webhook", "PaystackWebhook"))
                        {
                            await ReleaseOrderReservationIfActiveAsync(order, "Payment failed at gateway.", "PaystackWebhook", ct);
                        }
                    }
                }
            }
            else if (eventType is "refund.processed" or "refund.pending" or "refund.processing" or "refund.failed" or "refund.needs-attention")
            {
                if (!string.IsNullOrWhiteSpace(parsed.Reference))
                {
                    var found = await FindPaymentAndOrderByReferenceAsync(parsed.Reference, ct);
                    if (found is not null)
                    {
                        var (payment, order) = found.Value;
                        var refund = payment.Refunds
                            .OrderByDescending(r => r.CreatedAt)
                            .FirstOrDefault(r => r.Status != RefundStatus.Processed);

                        if (refund is not null)
                        {
                            var mappedStatus = eventType switch
                            {
                                "refund.processed" => RefundStatus.Processed,
                                "refund.processing" => RefundStatus.Processing,
                                "refund.failed" => RefundStatus.Failed,
                                "refund.needs-attention" => RefundStatus.NeedsAttention,
                                _ => RefundStatus.Pending
                            };

                            payment.ApplyRefundOutcome(refund, mappedStatus, parsed.ProviderRefundId, parsed.GatewayResponse, "PaystackWebhook");
                            SyncOrderStatusAfterRefund(order, payment);
                        }
                    }
                }
            }
            else if (eventType is "charge.dispute.create" or "transfer.reversed")
            {
                if (!string.IsNullOrWhiteSpace(parsed.Reference))
                {
                    var found = await FindPaymentAndOrderByReferenceAsync(parsed.Reference, ct);
                    if (found is not null)
                    {
                        var (payment, order) = found.Value;
                        payment.MarkReversed(parsed.GatewayResponse ?? eventType, "PaystackWebhook");
                        await EnsureReconciliationRecordAsync(
                            ReconciliationType.ReversedTransaction,
                            payment.Reference,
                            order.Id,
                            order.OrderNumber,
                            payment.Amount,
                            null,
                            payment.Currency,
                            $"Gateway reported reversal/dispute ({eventType}) for reference {payment.Reference}.",
                            ct);
                    }
                }
            }

            ev.MarkProcessed();
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing webhook event {WebhookEventId}", webhookEventId);
            ev.RecordFailure(ex.Message);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task ApplyVerifiedGatewayOutcomeAsync(
        Payment payment,
        Order order,
        PaymentVerifyResult verify,
        string actor,
        CancellationToken ct)
    {
        var providerStatus = (verify.ProviderStatus ?? string.Empty).Trim().ToLowerInvariant();

        if (providerStatus == "success")
        {
            // Verify currency & amount in kobo
            if (!string.Equals(verify.Currency, NigerianValueRules.DefaultCurrency, StringComparison.OrdinalIgnoreCase))
            {
                await EnsureReconciliationRecordAsync(
                    ReconciliationType.CurrencyMismatch,
                    payment.Reference,
                    order.Id,
                    order.OrderNumber,
                    payment.Amount,
                    NigerianValueRules.FromKobo(Math.Max(0, verify.AmountKobo)),
                    verify.Currency,
                    $"Expected currency {NigerianValueRules.DefaultCurrency} but gateway reported {verify.Currency}.",
                    ct);
                await db.SaveChangesAsync(ct);
                return;
            }

            if (verify.AmountKobo != payment.ExpectedAmountKobo)
            {
                await EnsureReconciliationRecordAsync(
                    ReconciliationType.AmountMismatch,
                    payment.Reference,
                    order.Id,
                    order.OrderNumber,
                    payment.Amount,
                    NigerianValueRules.FromKobo(Math.Max(0, verify.AmountKobo)),
                    verify.Currency,
                    $"Expected {payment.ExpectedAmountKobo} kobo (₦{payment.Amount:N2}) but gateway reported {verify.AmountKobo} kobo.",
                    ct);
                await db.SaveChangesAsync(ct);
                return;
            }

            if (verify.MetadataOrderId.HasValue && verify.MetadataOrderId.Value != Guid.Empty && verify.MetadataOrderId.Value != order.Id)
            {
                await EnsureReconciliationRecordAsync(
                    ReconciliationType.DuplicateReference,
                    payment.Reference,
                    order.Id,
                    order.OrderNumber,
                    payment.Amount,
                    NigerianValueRules.FromKobo(verify.AmountKobo),
                    verify.Currency,
                    $"Transaction metadata orderId ({verify.MetadataOrderId}) did not match local order ({order.Id}).",
                    ct);
                await db.SaveChangesAsync(ct);
                return;
            }

            var transitioned = payment.MarkVerifiedSuccess(
                verify.ProviderTransactionId,
                verify.Channel,
                verify.GatewayResponse,
                verify.PaidAtUtc,
                actor);

            if (!transitioned)
            {
                return;
            }

            // Commit inventory reservation idempotently (never decrement twice!)
            var reservation = await db.StockReservations
                .Include(r => r.Items)
                .FirstOrDefaultAsync(r => r.OrderId == order.Id, ct);

            var variantIds = order.Items.Select(i => i.VariantId).Distinct().ToList();
            var inventories = await db.InventoryItems
                .Where(inv => variantIds.Contains(inv.VariantId))
                .ToDictionaryAsync(inv => inv.VariantId, ct);

            if (reservation is not null && reservation.TryMarkCommitted())
            {
                foreach (var item in order.Items)
                {
                    if (inventories.TryGetValue(item.VariantId, out var inv))
                    {
                        var tx = inv.CommitReservedSale(item.Quantity, order.Id, actor);
                        db.InventoryTransactions.Add(tx);
                    }
                }
            }
            else if (reservation is null || reservation.Status is StockReservationStatus.Expired or StockReservationStatus.Released)
            {
                // Reservation had expired before late payment arrived -> attempt direct deduction if stock available, else record reconciliation
                var allAvailable = order.Items.All(i => inventories.TryGetValue(i.VariantId, out var inv) && inv.Available >= i.Quantity);
                if (allAvailable)
                {
                    foreach (var item in order.Items)
                    {
                        var inv = inventories[item.VariantId];
                        inv.Reserve(item.Quantity, order.Id, actor);
                        db.InventoryTransactions.Add(inv.CommitReservedSale(item.Quantity, order.Id, actor));
                    }
                }
                else
                {
                    await EnsureReconciliationRecordAsync(
                        ReconciliationType.PaidButStockUnavailable,
                        payment.Reference,
                        order.Id,
                        order.OrderNumber,
                        payment.Amount,
                        payment.Amount,
                        payment.Currency,
                        $"Late payment succeeded after stock reservation expired and stock is no longer sufficient for order {order.OrderNumber}.",
                        ct);
                }
            }

            var historyEntry = order.MarkConfirmedByVerifiedPayment(payment.Reference, actor);
            if (historyEntry is not null)
            {
                db.OrderStatusHistories.Add(historyEntry);
            }

            if (order.DeliveryMethod == DeliveryMethod.Delivery && order.AddressSnapshot is not null)
            {
                var existingDelivery = await db.OrderDeliveries.FirstOrDefaultAsync(d => d.OrderId == order.Id, ct);
                if (existingDelivery is null)
                {
                    var delivery = new OrderDelivery(
                        order.Id,
                        order.AddressSnapshot.RecipientName,
                        order.AddressSnapshot.PhoneNumber,
                        order.AddressSnapshot.FormatSingleLine(),
                        order.AddressSnapshot.State,
                        order.AddressSnapshot.City,
                        order.DeliveryInstructions,
                        actor);
                    db.OrderDeliveries.Add(delivery);
                }
            }

            await db.SaveChangesAsync(ct);

            await notifications.QueueAsync(
                order.UserId,
                order.ContactEmail,
                "PaymentConfirmed",
                $"Payment Confirmed for Order {order.OrderNumber}",
                $"We have verified your payment of ₦{payment.Amount:N2} (ref: {payment.Reference}). Your drinks order is now confirmed and queued for preparation.",
                order.Id,
                ct);
        }
        else if (providerStatus is "failed" or "abandoned")
        {
            var isAbandoned = providerStatus == "abandoned";
            if (payment.MarkFailedOrAbandoned(isAbandoned, verify.GatewayResponse, actor))
            {
                await db.SaveChangesAsync(ct);
                await notifications.QueueAsync(
                    order.UserId,
                    order.ContactEmail,
                    "PaymentFailed",
                    $"Payment {payment.Status} for Order {order.OrderNumber}",
                    $"Your payment attempt ({payment.Reference}) was {payment.Status.ToString().ToLowerInvariant()}. You can retry payment from your order details page while your reservation remains active.",
                    order.Id,
                    ct);
            }
        }
    }

    public async Task<RefundDto> RequestRefundAsync(Guid orderId, decimal amount, string reason, string? actorEmail, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new DomainException("Refund reason is required.", "validation_error", 400);
        }

        var order = await db.Orders
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == orderId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        var payment = await db.Payments
            .Include(p => p.Refunds)
            .Include(p => p.History)
            .FirstOrDefaultAsync(p => p.OrderId == order.Id, ct)
            ?? throw DomainException.NotFound("Payment record not found for order.");

        if (payment.Status is not (PaymentStatus.Successful or PaymentStatus.PartiallyRefunded or PaymentStatus.RefundPending))
        {
            throw DomainException.Conflict($"Cannot refund payment in status {payment.Status}.", "invalid_state_transition");
        }

        var refund = payment.BeginRefund(amount, reason, actorEmail);
        db.Refunds.Add(refund);

        if (order.Status is not (OrderStatus.RefundPending or OrderStatus.PartiallyRefunded or OrderStatus.Refunded))
        {
            if (Order.IsValidTransition(order.Status, OrderStatus.RefundPending))
            {
                var hist = order.TransitionTo(OrderStatus.RefundPending, $"Refund of ₦{refund.Amount:N2} requested: {reason}", actorEmail);
                db.OrderStatusHistories.Add(hist);
            }
        }

        await db.SaveChangesAsync(ct);

        // Call gateway to initiate refund (note: refund is NOT marked Processed until provider confirms Processed)
        var gwResult = await paymentGateway.RequestRefundAsync(
            new RefundGatewayRequest(
                payment.Reference,
                NigerianValueRules.ToKobo(refund.Amount),
                refund.Currency,
                refund.Reason),
            ct);

        payment.ApplyRefundOutcome(
            refund,
            gwResult.Succeeded ? gwResult.Status : RefundStatus.Failed,
            gwResult.ProviderRefundId,
            gwResult.Succeeded ? null : gwResult.Message,
            actorEmail ?? "Admin");

        SyncOrderStatusAfterRefund(order, payment);
        audit.Record("RefundRequested", "Refund", refund.Id.ToString(), $"Order {order.OrderNumber}, Amount ₦{refund.Amount:N2}, GatewayStatus: {refund.Status}");
        await db.SaveChangesAsync(ct);

        await notifications.QueueAsync(
            order.UserId,
            order.ContactEmail,
            "RefundUpdate",
            $"Refund Initiated for Order {order.OrderNumber}",
            $"A refund of ₦{refund.Amount:N2} for order {order.OrderNumber} has been submitted (current status: {refund.Status}).",
            order.Id,
            ct);

        return MapRefund(refund);
    }

    public async Task<RefundDto> SyncRefundStatusAsync(Guid refundId, string? actorEmail, CancellationToken ct = default)
    {
        var refund = await db.Refunds.FirstOrDefaultAsync(r => r.Id == refundId, ct)
            ?? throw DomainException.NotFound("Refund not found.");

        var payment = await db.Payments
            .Include(p => p.Refunds)
            .Include(p => p.History)
            .FirstOrDefaultAsync(p => p.Id == refund.PaymentId, ct)
            ?? throw DomainException.NotFound("Payment not found.");

        var order = await db.Orders
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == refund.OrderId, ct)
            ?? throw DomainException.NotFound("Order not found.");

        if (refund.Status is RefundStatus.Pending or RefundStatus.Processing or RefundStatus.Requested)
        {
            // In Simulated mode or when provider confirms completion on sync:
            if (string.Equals(paystackOptions.Value.Provider, "Simulated", StringComparison.OrdinalIgnoreCase))
            {
                payment.ApplyRefundOutcome(refund, RefundStatus.Processed, refund.ProviderRefundId ?? $"rf_{Guid.NewGuid():N}"[..15], null, actorEmail ?? "AdminSync");
                SyncOrderStatusAfterRefund(order, payment);
                audit.Record("RefundSynced", "Refund", refund.Id.ToString(), $"Refund marked Processed for order {order.OrderNumber}");
                await db.SaveChangesAsync(ct);

                await notifications.QueueAsync(
                    order.UserId,
                    order.ContactEmail,
                    "RefundProcessed",
                    $"Refund Completed for Order {order.OrderNumber}",
                    $"Your refund of ₦{refund.Amount:N2} for order {order.OrderNumber} has been processed.",
                    order.Id,
                    ct);
            }
        }

        return MapRefund(refund);
    }

    public async Task<ReconciliationRunResultDto> RunReconciliationAsync(string? actor, CancellationToken ct = default)
    {
        var pendingPayments = await db.Payments
            .Where(p => p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Initialized)
            .OrderByDescending(p => p.CreatedAt)
            .Take(100)
            .ToListAsync(ct);

        var exceptionsBefore = await db.ReconciliationRecords.CountAsync(ct);

        foreach (var payment in pendingPayments)
        {
            var order = await db.Orders
                .Include(o => o.Items)
                .FirstOrDefaultAsync(o => o.Id == payment.OrderId, ct);

            if (order is null)
            {
                continue;
            }

            var verify = await paymentGateway.VerifyPaymentAsync(payment.Reference, ct);
            if (verify.Succeeded && string.Equals(verify.ProviderStatus, "success", StringComparison.OrdinalIgnoreCase))
            {
                await EnsureReconciliationRecordAsync(
                    ReconciliationType.PendingPaymentSucceededAtGateway,
                    payment.Reference,
                    order.Id,
                    order.OrderNumber,
                    payment.Amount,
                    NigerianValueRules.FromKobo(verify.AmountKobo),
                    verify.Currency,
                    $"Reconciliation discovered local {payment.Status} payment {payment.Reference} succeeded at Paystack and applied verification.",
                    ct);

                await ApplyVerifiedGatewayOutcomeAsync(payment, order, verify, actor ?? "ReconciliationJob", ct);
            }
        }

        await db.SaveChangesAsync(ct);
        var exceptionsAfter = await db.ReconciliationRecords.CountAsync(ct);
        return new ReconciliationRunResultDto(pendingPayments.Count, Math.Max(0, exceptionsAfter - exceptionsBefore));
    }

    public async Task<int> CleanupExpiredReservationsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expiredReservations = await db.StockReservations
            .Include(r => r.Items)
            .Where(r => r.Status == StockReservationStatus.Active && r.ExpiresAt <= now)
            .Take(50)
            .ToListAsync(ct);

        var releasedCount = 0;
        foreach (var res in expiredReservations)
        {
            var order = await db.Orders
                .Include(o => o.StatusHistory)
                .FirstOrDefaultAsync(o => o.Id == res.OrderId, ct);
            var payment = await db.Payments.FirstOrDefaultAsync(p => p.OrderId == res.OrderId, ct);

            if (payment?.Status == PaymentStatus.Successful)
            {
                res.TryMarkCommitted();
                continue;
            }

            if (res.TryMarkReleased(expired: true))
            {
                foreach (var item in res.Items)
                {
                    var inv = await db.InventoryItems.FirstOrDefaultAsync(i => i.VariantId == item.VariantId, ct);
                    if (inv is not null)
                    {
                        db.InventoryTransactions.Add(inv.ReleaseReservation(item.Quantity, res.OrderId, "Reservation expired before payment completion.", "ReservationCleanupJob"));
                    }
                }

                if (order is not null && order.Status == OrderStatus.PendingPayment)
                {
                    var hist = order.TransitionTo(OrderStatus.Cancelled, "Order cancelled automatically because payment reservation expired.", "ReservationCleanupJob");
                    db.OrderStatusHistories.Add(hist);
                }

                if (payment is not null && payment.Status is PaymentStatus.Initialized or PaymentStatus.Pending)
                {
                    payment.MarkFailedOrAbandoned(abandoned: true, "Payment reservation expired.", "ReservationCleanupJob");
                }

                releasedCount++;
            }
        }

        if (releasedCount > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        return releasedCount;
    }

    public async Task ReleaseOrderReservationIfActiveAsync(Order order, string reason, string? actor, CancellationToken ct)
    {
        var reservation = await db.StockReservations
            .Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.OrderId == order.Id, ct);

        if (reservation is not null && reservation.TryMarkReleased(expired: false))
        {
            foreach (var item in reservation.Items)
            {
                var inv = await db.InventoryItems.FirstOrDefaultAsync(i => i.VariantId == item.VariantId, ct);
                if (inv is not null)
                {
                    db.InventoryTransactions.Add(inv.ReleaseReservation(item.Quantity, order.Id, reason, actor));
                }
            }
        }
    }

    private static void SyncOrderStatusAfterRefund(Order order, Payment payment)
    {
        if (payment.Status == PaymentStatus.Refunded && Order.IsValidTransition(order.Status, OrderStatus.Refunded))
        {
            order.TransitionTo(OrderStatus.Refunded, "Full refund processed.", "PaystackRefund");
        }
        else if (payment.Status == PaymentStatus.PartiallyRefunded && Order.IsValidTransition(order.Status, OrderStatus.PartiallyRefunded))
        {
            order.TransitionTo(OrderStatus.PartiallyRefunded, $"Partial refund processed (₦{payment.RefundedAmount:N2}).", "PaystackRefund");
        }
    }

    private async Task<(Payment Payment, Order Order)?> FindPaymentAndOrderByReferenceAsync(string reference, CancellationToken ct)
    {
        var trimmed = reference.Trim();
        var payment = await db.Payments
            .Include(p => p.Attempts)
            .Include(p => p.Refunds)
            .Include(p => p.History)
            .FirstOrDefaultAsync(p => p.Reference == trimmed || p.Attempts.Any(a => a.Reference == trimmed), ct);

        if (payment is null)
        {
            return null;
        }

        var order = await db.Orders
            .Include(o => o.Items)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.Id == payment.OrderId, ct);

        if (order is null)
        {
            return null;
        }

        return (payment, order);
    }

    private async Task EnsureReconciliationRecordAsync(
        ReconciliationType type,
        string? reference,
        Guid? orderId,
        string? orderNumber,
        decimal? expectedAmount,
        decimal? actualAmount,
        string? currency,
        string description,
        CancellationToken ct)
    {
        var exists = await db.ReconciliationRecords.AnyAsync(
            r => r.Type == type && r.PaymentReference == reference && r.Status == ReconciliationStatus.Open,
            ct);

        if (!exists)
        {
            db.ReconciliationRecords.Add(new ReconciliationRecord(
                type,
                reference,
                orderId,
                orderNumber,
                expectedAmount,
                actualAmount,
                currency,
                description));
        }
    }

    private static PaymentStatusDto MapPaymentStatus(Payment payment, Order order, string? customMessage = null)
    {
        var message = customMessage ?? payment.Status switch
        {
            PaymentStatus.Successful => "Payment verified and confirmed.",
            PaymentStatus.Pending or PaymentStatus.Initialized => "Payment is pending confirmation from Paystack.",
            PaymentStatus.Failed => payment.GatewayResponse ?? "Payment failed.",
            PaymentStatus.Abandoned => "Payment was abandoned before completion.",
            PaymentStatus.Refunded => "Payment has been refunded.",
            PaymentStatus.PartiallyRefunded => $"Payment partially refunded (₦{payment.RefundedAmount:N2}).",
            _ => $"Payment status: {payment.Status}."
        };

        return new PaymentStatusDto(
            payment.Reference,
            order.Id.ToString(),
            order.OrderNumber,
            payment.Status,
            payment.Amount,
            payment.Currency,
            payment.PaidAt,
            payment.Channel,
            order.Status,
            message);
    }

    public static RefundDto MapRefund(Refund r) =>
        new(
            r.Id.ToString(),
            r.PaymentId.ToString(),
            r.PaymentReference,
            r.OrderId.ToString(),
            r.OrderNumber,
            r.Amount,
            r.Currency,
            r.Status,
            r.Reason,
            r.ProviderRefundId,
            r.RequestedBy,
            r.FailureReason,
            r.CreatedAt,
            r.ProcessedAt);
}
