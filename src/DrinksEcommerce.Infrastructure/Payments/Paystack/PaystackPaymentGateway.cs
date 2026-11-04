using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DrinksEcommerce.Application.Common;
using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DrinksEcommerce.Infrastructure.Payments.Paystack;

public sealed class SimulatedTransactionState
{
    public required string Reference { get; init; }
    public required string CustomerEmail { get; init; }
    public required long AmountKobo { get; init; }
    public required string Currency { get; init; }
    public required string CallbackUrl { get; init; }
    public required Guid OrderId { get; init; }
    public required string OrderNumber { get; init; }
    public string ProviderStatus { get; set; } = "ongoing";
    public string? GatewayResponse { get; set; } = "Waiting for customer authorization";
    public string? Channel { get; set; } = "card";
    public DateTimeOffset? PaidAtUtc { get; set; }
}

public sealed class SimulatedPaymentStore
{
    private readonly ConcurrentDictionary<string, SimulatedTransactionState> _states = new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(SimulatedTransactionState state) => _states[state.Reference] = state;
    public SimulatedTransactionState? Get(string reference) =>
        _states.TryGetValue(reference, out var state) ? state : null;
}

public sealed class PaystackPaymentGateway(
    HttpClient httpClient,
    SimulatedPaymentStore simulatedStore,
    IOptions<PaystackOptions> options,
    ILogger<PaystackPaymentGateway> logger) : IPaymentGateway
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private bool UseSimulator =>
        string.Equals(options.Value.Provider, "Simulated", StringComparison.OrdinalIgnoreCase);

    public async Task<PaymentInitializeResult> InitializePaymentAsync(
        PaymentInitializeRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.AmountKobo <= 0)
        {
            throw new DomainException("Paystack initialization amount in kobo must be greater than zero.", "invalid_amount", 400);
        }

        if (UseSimulator)
        {
            simulatedStore.Upsert(new SimulatedTransactionState
            {
                Reference = request.Reference,
                CustomerEmail = request.CustomerEmail,
                AmountKobo = request.AmountKobo,
                Currency = request.Currency,
                CallbackUrl = request.CallbackUrl,
                OrderId = request.OrderId,
                OrderNumber = request.OrderNumber,
                ProviderStatus = "ongoing"
            });

            var simBase = options.Value.SimulatorBaseUrl.TrimEnd('/');
            var authUrl = $"{simBase}/dev/paystack/checkout?reference={Uri.EscapeDataString(request.Reference)}&callbackUrl={Uri.EscapeDataString(request.CallbackUrl)}";
            return new PaymentInitializeResult(true, request.Reference, authUrl, $"sim_acc_{request.Reference}", null, false);
        }

        try
        {
            using var reqMsg = new HttpRequestMessage(HttpMethod.Post, "transaction/initialize");
            reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.SecretKey);
            var payload = new
            {
                email = request.CustomerEmail,
                amount = request.AmountKobo.ToString(),
                currency = request.Currency,
                reference = request.Reference,
                callback_url = request.CallbackUrl,
                metadata = new
                {
                    order_id = request.OrderId.ToString(),
                    order_number = request.OrderNumber
                }
            };
            reqMsg.Content = JsonContent.Create(payload, options: JsonOpts);

            using var resp = await httpClient.SendAsync(reqMsg, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Paystack initialize returned HTTP {StatusCode} for reference {Reference}", (int)resp.StatusCode, request.Reference);
                return new PaymentInitializeResult(false, request.Reference, null, null, $"Payment gateway returned {(int)resp.StatusCode}.", false);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var sProp) && sProp.GetBoolean();
            var message = root.TryGetProperty("message", out var mProp) ? mProp.GetString() : null;

            if (!status || !root.TryGetProperty("data", out var data))
            {
                return new PaymentInitializeResult(false, request.Reference, null, null, message ?? "Paystack initialization rejected.", false);
            }

            var authUrl = data.TryGetProperty("authorization_url", out var urlProp) ? urlProp.GetString() : null;
            var accessCode = data.TryGetProperty("access_code", out var acProp) ? acProp.GetString() : null;

            return new PaymentInitializeResult(true, request.Reference, authUrl, accessCode, null, false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Paystack initialize timed out for reference {Reference}", request.Reference);
            return new PaymentInitializeResult(false, request.Reference, null, null, "Paystack initialization timed out.", IsAmbiguousTimeout: true);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Paystack initialize network failure for reference {Reference}", request.Reference);
            return new PaymentInitializeResult(false, request.Reference, null, null, "Unable to reach Paystack gateway.", IsAmbiguousTimeout: true);
        }
    }

    public async Task<PaymentVerifyResult> VerifyPaymentAsync(string reference, CancellationToken cancellationToken = default)
    {
        if (UseSimulator)
        {
            var sim = simulatedStore.Get(reference);
            if (sim is null)
            {
                return new PaymentVerifyResult(
                    true,
                    reference,
                    "ongoing",
                    0,
                    NigerianValueRules.DefaultCurrency,
                    null,
                    null,
                    "Awaiting simulated customer authorization",
                    null,
                    null,
                    null);
            }

            return new PaymentVerifyResult(
                true,
                sim.Reference,
                sim.ProviderStatus,
                sim.AmountKobo,
                sim.Currency,
                $"sim_tx_{sim.Reference}",
                sim.Channel,
                sim.GatewayResponse,
                sim.PaidAtUtc,
                sim.OrderId,
                null);
        }

        try
        {
            using var reqMsg = new HttpRequestMessage(HttpMethod.Get, $"transaction/verify/{Uri.EscapeDataString(reference)}");
            reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.SecretKey);

            using var resp = await httpClient.SendAsync(reqMsg, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Paystack verify returned HTTP {StatusCode} for reference {Reference}", (int)resp.StatusCode, reference);
                return new PaymentVerifyResult(false, reference, "unknown", 0, "NGN", null, null, null, null, null, $"Paystack verification returned HTTP {(int)resp.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var sProp) || !sProp.GetBoolean() || !root.TryGetProperty("data", out var data))
            {
                var errMsg = root.TryGetProperty("message", out var mProp) ? mProp.GetString() : "Verification failed.";
                return new PaymentVerifyResult(false, reference, "unknown", 0, "NGN", null, null, null, null, null, errMsg);
            }

            var providerStatus = data.TryGetProperty("status", out var stProp) ? stProp.GetString() ?? "unknown" : "unknown";
            var amountKobo = data.TryGetProperty("amount", out var amtProp) ? amtProp.GetInt64() : 0L;
            var currency = data.TryGetProperty("currency", out var curProp) ? curProp.GetString() ?? "NGN" : "NGN";
            var txId = data.TryGetProperty("id", out var idProp) ? idProp.ToString() : null;
            var channel = data.TryGetProperty("channel", out var chProp) ? chProp.GetString() : null;
            var gwResp = data.TryGetProperty("gateway_response", out var gwProp) ? gwProp.GetString() : null;
            DateTimeOffset? paidAt = null;
            if (data.TryGetProperty("paid_at", out var paidProp) && paidProp.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(paidProp.GetString(), out var parsedPaid))
            {
                paidAt = parsedPaid.ToUniversalTime();
            }

            Guid? metadataOrderId = null;
            if (data.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                meta.TryGetProperty("order_id", out var ordIdProp) && Guid.TryParse(ordIdProp.GetString(), out var parsedOrderId))
            {
                metadataOrderId = parsedOrderId;
            }

            return new PaymentVerifyResult(
                true,
                reference,
                providerStatus,
                amountKobo,
                currency,
                txId,
                channel,
                gwResp,
                paidAt,
                metadataOrderId,
                null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack verification error for reference {Reference}", reference);
            return new PaymentVerifyResult(false, reference, "unknown", 0, "NGN", null, null, null, null, null, "Unable to verify transaction with Paystack.");
        }
    }

    public async Task<RefundGatewayResult> RequestRefundAsync(
        RefundGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        if (UseSimulator)
        {
            // Per requirement: do not mark a refund complete merely because the refund request was accepted.
            // It returns Pending until a webhook (refund.processed) or sync confirms completion.
            var simRefundId = $"rf_sim_{Guid.NewGuid():N}"[..18];
            return new RefundGatewayResult(true, RefundStatus.Pending, simRefundId, "Refund accepted by simulator and pending settlement.");
        }

        try
        {
            using var reqMsg = new HttpRequestMessage(HttpMethod.Post, "refund");
            reqMsg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.SecretKey);
            reqMsg.Content = JsonContent.Create(new
            {
                transaction = request.TransactionReference,
                amount = request.AmountKobo,
                currency = request.Currency,
                merchant_note = request.Reason
            }, options: JsonOpts);

            using var resp = await httpClient.SendAsync(reqMsg, cancellationToken);
            var body = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                return new RefundGatewayResult(false, RefundStatus.Failed, null, $"Paystack refund request returned HTTP {(int)resp.StatusCode}.");
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var sProp) || !sProp.GetBoolean() || !root.TryGetProperty("data", out var data))
            {
                var msg = root.TryGetProperty("message", out var mProp) ? mProp.GetString() : "Refund rejected.";
                return new RefundGatewayResult(false, RefundStatus.Failed, null, msg);
            }

            var providerId = data.TryGetProperty("id", out var idProp) ? idProp.ToString() : null;
            var statusStr = data.TryGetProperty("status", out var stProp) ? stProp.GetString()?.ToLowerInvariant() : "pending";
            var mappedStatus = statusStr switch
            {
                "processed" => RefundStatus.Processed,
                "processing" => RefundStatus.Processing,
                "failed" => RefundStatus.Failed,
                "needs-attention" => RefundStatus.NeedsAttention,
                _ => RefundStatus.Pending
            };

            return new RefundGatewayResult(true, mappedStatus, providerId, "Refund submitted to Paystack.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Paystack refund request failed for reference {Reference}", request.TransactionReference);
            return new RefundGatewayResult(false, RefundStatus.Failed, null, "Error communicating with Paystack refund endpoint.");
        }
    }

    public bool VerifyWebhookSignature(string rawPayload, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(rawPayload) || string.IsNullOrWhiteSpace(signatureHeader))
        {
            return false;
        }

        var secret = options.Value.SecretKey;
        if (string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        var expectedHex = ComputeHmacSha512Hex(rawPayload, secret);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedHex);
        var providedBytes = Encoding.UTF8.GetBytes(signatureHeader.Trim().ToLowerInvariant());

        return expectedBytes.Length == providedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    public static string ComputeHmacSha512Hex(string rawPayload, string secretKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secretKey);
        var payloadBytes = Encoding.UTF8.GetBytes(rawPayload);
        var hash = HMACSHA512.HashData(keyBytes, payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public ParsedWebhookEvent ParseWebhookEvent(string rawPayload)
    {
        using var doc = JsonDocument.Parse(rawPayload);
        var root = doc.RootElement;

        var eventType = root.TryGetProperty("event", out var evProp) ? evProp.GetString() ?? "unknown" : "unknown";
        string? reference = null;
        long? amountKobo = null;
        string? currency = null;
        string? providerStatus = null;
        string? txId = null;
        string? channel = null;
        string? gwResp = null;
        string? refundId = null;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("reference", out var refProp) && refProp.ValueKind == JsonValueKind.String)
            {
                reference = refProp.GetString();
            }
            else if (data.TryGetProperty("transaction_reference", out var txRefProp) && txRefProp.ValueKind == JsonValueKind.String)
            {
                reference = txRefProp.GetString();
            }

            if (data.TryGetProperty("amount", out var amtProp) && amtProp.TryGetInt64(out var parsedAmt))
            {
                amountKobo = parsedAmt;
            }
            if (data.TryGetProperty("currency", out var curProp) && curProp.ValueKind == JsonValueKind.String)
            {
                currency = curProp.GetString();
            }
            if (data.TryGetProperty("status", out var stProp) && stProp.ValueKind == JsonValueKind.String)
            {
                providerStatus = stProp.GetString();
            }
            if (data.TryGetProperty("id", out var idProp))
            {
                var idStr = idProp.ToString();
                if (eventType.StartsWith("refund.", StringComparison.OrdinalIgnoreCase))
                {
                    refundId = idStr;
                }
                else
                {
                    txId = idStr;
                }
            }
            if (data.TryGetProperty("channel", out var chProp) && chProp.ValueKind == JsonValueKind.String)
            {
                channel = chProp.GetString();
            }
            if (data.TryGetProperty("gateway_response", out var gwProp) && gwProp.ValueKind == JsonValueKind.String)
            {
                gwResp = gwProp.GetString();
            }
        }

        // Deterministic deduplication key from eventType + reference + txId/refundId + payload hash
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawPayload)))[..24].ToLowerInvariant();
        var dedupKey = $"{eventType}:{reference ?? "noref"}:{txId ?? refundId ?? payloadHash}";
        if (dedupKey.Length > 120)
        {
            dedupKey = dedupKey[..120];
        }

        return new ParsedWebhookEvent(
            eventType,
            dedupKey,
            reference,
            amountKobo,
            currency,
            providerStatus,
            txId,
            channel,
            gwResp,
            refundId);
    }
}
