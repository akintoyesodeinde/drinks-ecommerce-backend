using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Enums;

namespace DrinksEcommerce.Domain.Payments;

public class Payment : BaseEntity
{
    public Guid OrderId { get; private set; }
    public string OrderNumber { get; private set; } = string.Empty;
    public string CustomerEmail { get; private set; } = string.Empty;
    public string Reference { get; private set; } = string.Empty;
    public PaymentStatus Status { get; private set; } = PaymentStatus.Initialized;
    public decimal Amount { get; private set; }
    public long ExpectedAmountKobo { get; private set; }
    public string Currency { get; private set; } = NigerianValueRules.DefaultCurrency;
    public decimal RefundedAmount { get; private set; }
    public string? AuthorizationUrl { get; private set; }
    public string? AccessCode { get; private set; }
    public string? Channel { get; private set; }
    public string? GatewayResponse { get; private set; }
    public string? ProviderTransactionId { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }
    public int VerificationFailureCount { get; private set; }
    public byte[] RowVersion { get; set; } = Guid.NewGuid().ToByteArray();

    public ICollection<PaymentAttempt> Attempts { get; private set; } = new List<PaymentAttempt>();
    public ICollection<Refund> Refunds { get; private set; } = new List<Refund>();
    public ICollection<PaymentStatusHistory> History { get; private set; } = new List<PaymentStatusHistory>();

    public decimal RefundableAmount =>
        Status is PaymentStatus.Successful or PaymentStatus.PartiallyRefunded or PaymentStatus.RefundPending
            ? Math.Max(0m, Amount - RefundedAmount)
            : 0m;

    private Payment() { }

    public Payment(Guid orderId, string orderNumber, string customerEmail, decimal amountNgn, string reference)
    {
        OrderId = orderId;
        OrderNumber = orderNumber;
        CustomerEmail = customerEmail.Trim();
        Amount = NigerianValueRules.RoundNaira(amountNgn);
        ExpectedAmountKobo = NigerianValueRules.ToKobo(Amount);
        Currency = NigerianValueRules.DefaultCurrency;
        Reference = reference.Trim();
        Status = PaymentStatus.Initialized;
        History.Add(new PaymentStatusHistory(Id, Status, $"Payment initialized with reference {Reference}.", "System"));
    }

    public PaymentAttempt RecordAttempt(string reference, string? authorizationUrl, string? accessCode, string status, string? errorMessage)
    {
        Reference = reference.Trim();
        if (!string.IsNullOrWhiteSpace(authorizationUrl))
        {
            AuthorizationUrl = authorizationUrl;
            AccessCode = accessCode;
            if (Status is PaymentStatus.Initialized or PaymentStatus.Failed or PaymentStatus.Abandoned)
            {
                Status = PaymentStatus.Pending;
                History.Add(new PaymentStatusHistory(Id, Status, $"Paystack authorization initialized (ref: {Reference}).", "System"));
            }
        }

        BumpConcurrency();
        var attempt = new PaymentAttempt(Id, Reference, status, authorizationUrl, errorMessage);
        Attempts.Add(attempt);
        return attempt;
    }

    public bool MarkVerifiedSuccess(string? providerTransactionId, string? channel, string? gatewayResponse, DateTimeOffset? paidAtUtc, string actor = "PaystackVerifier")
    {
        if (Status is PaymentStatus.Successful or PaymentStatus.RefundPending or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded)
        {
            return false;
        }
        if (Status == PaymentStatus.Reversed)
        {
            throw DomainException.Conflict("Cannot mark a reversed payment as successful.", "invalid_state_transition");
        }

        Status = PaymentStatus.Successful;
        ProviderTransactionId = providerTransactionId ?? ProviderTransactionId;
        Channel = channel ?? Channel;
        GatewayResponse = gatewayResponse ?? "Approved";
        PaidAt = paidAtUtc ?? DateTimeOffset.UtcNow;
        BumpConcurrency();
        History.Add(new PaymentStatusHistory(Id, Status, $"Verified successful payment via {Channel ?? "Paystack"} (tx: {ProviderTransactionId ?? "n/a"}).", actor));
        return true;
    }

    public bool MarkFailedOrAbandoned(bool abandoned, string? gatewayResponse, string actor = "PaystackVerifier")
    {
        if (Status is PaymentStatus.Successful or PaymentStatus.RefundPending or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded)
        {
            return false;
        }

        var next = abandoned ? PaymentStatus.Abandoned : PaymentStatus.Failed;
        if (Status == next)
        {
            return false;
        }

        Status = next;
        GatewayResponse = gatewayResponse;
        BumpConcurrency();
        History.Add(new PaymentStatusHistory(Id, Status, $"Payment {next}: {gatewayResponse ?? "Not completed"}.", actor));
        return true;
    }

    public void MarkReversed(string? reason, string actor = "PaystackWebhook")
    {
        Status = PaymentStatus.Reversed;
        GatewayResponse = reason;
        BumpConcurrency();
        History.Add(new PaymentStatusHistory(Id, Status, $"Transaction reversed: {reason ?? "Chargeback/reversal"}.", actor));
    }

    public void RecordVerificationFailure()
    {
        VerificationFailureCount++;
        BumpConcurrency();
    }

    public Refund BeginRefund(decimal amountNgn, string reason, string? requestedBy)
    {
        var amount = NigerianValueRules.RoundNaira(amountNgn);
        if (amount <= 0m)
        {
            throw new DomainException("Refund amount must be greater than zero.", "validation_error", 400);
        }

        var committedOrPendingRefunds = Refunds
            .Where(r => r.Status != RefundStatus.Failed)
            .Sum(r => r.Amount);

        if (committedOrPendingRefunds + amount > Amount)
        {
            throw DomainException.Conflict(
                $"Cumulative refunds (₦{committedOrPendingRefunds + amount:N2}) cannot exceed the paid amount (₦{Amount:N2}).",
                "refund_limit_exceeded");
        }

        Status = PaymentStatus.RefundPending;
        BumpConcurrency();
        var refund = new Refund(Id, Reference, OrderId, OrderNumber, amount, reason, requestedBy);
        Refunds.Add(refund);
        History.Add(new PaymentStatusHistory(Id, Status, $"Refund of ₦{amount:N2} requested by {requestedBy ?? "Admin"}.", requestedBy));
        return refund;
    }

    public void ApplyRefundOutcome(Refund refund, RefundStatus newStatus, string? providerRefundId, string? failureReason, string actor = "PaystackRefund")
    {
        var wasProcessed = refund.Status == RefundStatus.Processed;
        refund.UpdateStatus(newStatus, providerRefundId, failureReason);

        if (!wasProcessed && newStatus == RefundStatus.Processed)
        {
            RefundedAmount = NigerianValueRules.RoundNaira(Math.Min(Amount, RefundedAmount + refund.Amount));
        }

        var hasActiveRefunds = Refunds.Any(r => r.Status is RefundStatus.Requested or RefundStatus.Pending or RefundStatus.Processing or RefundStatus.NeedsAttention);
        if (RefundedAmount >= Amount)
        {
            Status = PaymentStatus.Refunded;
        }
        else if (hasActiveRefunds)
        {
            Status = PaymentStatus.RefundPending;
        }
        else if (RefundedAmount > 0m)
        {
            Status = PaymentStatus.PartiallyRefunded;
        }
        else
        {
            Status = PaymentStatus.Successful;
        }

        BumpConcurrency();
        History.Add(new PaymentStatusHistory(Id, Status, $"Refund {refund.Id} status updated to {newStatus}. Total refunded: ₦{RefundedAmount:N2}.", actor));
    }

    public static bool IsValidTransition(PaymentStatus current, PaymentStatus next) => (current, next) switch
    {
        (PaymentStatus.Initialized, PaymentStatus.Pending) => true,
        (PaymentStatus.Initialized, PaymentStatus.Successful) => true,
        (PaymentStatus.Initialized, PaymentStatus.Failed) => true,
        (PaymentStatus.Initialized, PaymentStatus.Abandoned) => true,
        (PaymentStatus.Pending, PaymentStatus.Successful) => true,
        (PaymentStatus.Pending, PaymentStatus.Failed) => true,
        (PaymentStatus.Pending, PaymentStatus.Abandoned) => true,
        (PaymentStatus.Failed, PaymentStatus.Pending) => true,
        (PaymentStatus.Failed, PaymentStatus.Successful) => true,
        (PaymentStatus.Abandoned, PaymentStatus.Pending) => true,
        (PaymentStatus.Abandoned, PaymentStatus.Successful) => true,
        (PaymentStatus.Successful, PaymentStatus.RefundPending) => true,
        (PaymentStatus.Successful, PaymentStatus.Reversed) => true,
        (PaymentStatus.RefundPending, PaymentStatus.PartiallyRefunded) => true,
        (PaymentStatus.RefundPending, PaymentStatus.Refunded) => true,
        (PaymentStatus.RefundPending, PaymentStatus.Successful) => true,
        (PaymentStatus.PartiallyRefunded, PaymentStatus.RefundPending) => true,
        (PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded) => true,
        _ => false
    };

    private void BumpConcurrency()
    {
        RowVersion = Guid.NewGuid().ToByteArray();
        Touch();
    }
}

public class PaymentAttempt : BaseEntity
{
    public Guid PaymentId { get; private set; }
    public string Reference { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string? AuthorizationUrl { get; private set; }
    public string? ErrorMessage { get; private set; }

    private PaymentAttempt() { }

    public PaymentAttempt(Guid paymentId, string reference, string status, string? authorizationUrl, string? errorMessage)
    {
        PaymentId = paymentId;
        Reference = reference;
        Status = status;
        AuthorizationUrl = authorizationUrl;
        ErrorMessage = errorMessage;
    }
}

public class PaymentStatusHistory : BaseEntity
{
    public Guid PaymentId { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public string? Note { get; private set; }
    public string? Actor { get; private set; }

    private PaymentStatusHistory() { }

    public PaymentStatusHistory(Guid paymentId, PaymentStatus status, string? note, string? actor)
    {
        PaymentId = paymentId;
        Status = status.ToString();
        Note = note;
        Actor = actor;
    }
}

public class PaymentWebhookEvent : BaseEntity
{
    public string DedupKey { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public string? Reference { get; private set; }
    public string RawPayload { get; private set; } = string.Empty;
    public bool IsProcessed { get; private set; }
    public int AttemptCount { get; private set; }
    public string? ProcessingError { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    private PaymentWebhookEvent() { }

    public PaymentWebhookEvent(string dedupKey, string eventType, string? reference, string rawPayload)
    {
        DedupKey = dedupKey;
        EventType = eventType;
        Reference = reference;
        RawPayload = rawPayload;
    }

    public void MarkProcessed()
    {
        IsProcessed = true;
        AttemptCount++;
        ProcessingError = null;
        ProcessedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    public void RecordFailure(string error)
    {
        AttemptCount++;
        ProcessingError = error.Length > 1000 ? error[..1000] : error;
        Touch();
    }
}

public class Refund : BaseEntity
{
    public Guid PaymentId { get; private set; }
    public string PaymentReference { get; private set; } = string.Empty;
    public Guid OrderId { get; private set; }
    public string OrderNumber { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = NigerianValueRules.DefaultCurrency;
    public RefundStatus Status { get; private set; } = RefundStatus.Requested;
    public string Reason { get; private set; } = string.Empty;
    public string? ProviderRefundId { get; private set; }
    public string? RequestedBy { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    private Refund() { }

    public Refund(Guid paymentId, string paymentReference, Guid orderId, string orderNumber, decimal amount, string reason, string? requestedBy)
    {
        PaymentId = paymentId;
        PaymentReference = paymentReference;
        OrderId = orderId;
        OrderNumber = orderNumber;
        Amount = NigerianValueRules.RoundNaira(amount);
        Reason = reason.Trim();
        RequestedBy = requestedBy;
        Status = RefundStatus.Requested;
    }

    public void UpdateStatus(RefundStatus status, string? providerRefundId, string? failureReason = null)
    {
        Status = status;
        if (!string.IsNullOrWhiteSpace(providerRefundId))
        {
            ProviderRefundId = providerRefundId;
        }
        FailureReason = failureReason;
        if (status == RefundStatus.Processed)
        {
            ProcessedAt = DateTimeOffset.UtcNow;
        }
        Touch();
    }
}

public class ReconciliationRecord : BaseEntity
{
    public ReconciliationType Type { get; private set; }
    public ReconciliationStatus Status { get; private set; } = ReconciliationStatus.Open;
    public string? PaymentReference { get; private set; }
    public Guid? OrderId { get; private set; }
    public string? OrderNumber { get; private set; }
    public decimal? ExpectedAmount { get; private set; }
    public decimal? ActualAmount { get; private set; }
    public string? Currency { get; private set; }
    public string Description { get; private set; } = string.Empty;
    public DateTimeOffset? ResolvedAt { get; private set; }
    public string? ResolvedBy { get; private set; }
    public string? ResolutionNote { get; private set; }

    private ReconciliationRecord() { }

    public ReconciliationRecord(
        ReconciliationType type,
        string? paymentReference,
        Guid? orderId,
        string? orderNumber,
        decimal? expectedAmount,
        decimal? actualAmount,
        string? currency,
        string description)
    {
        Type = type;
        PaymentReference = paymentReference;
        OrderId = orderId;
        OrderNumber = orderNumber;
        ExpectedAmount = expectedAmount;
        ActualAmount = actualAmount;
        Currency = currency;
        Description = description;
        Status = ReconciliationStatus.Open;
    }

    public void Resolve(string note, string? resolvedBy)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            throw new DomainException("Resolution note is required.", "validation_error", 400);
        }

        Status = ReconciliationStatus.Resolved;
        ResolutionNote = note.Trim();
        ResolvedBy = resolvedBy;
        ResolvedAt = DateTimeOffset.UtcNow;
        Touch();
    }
}
