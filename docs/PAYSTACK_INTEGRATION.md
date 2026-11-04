# DrinksEcommerce — Paystack Integration

## 1. Gateway Architecture

`IPaymentGateway` is defined in `DrinksEcommerce.Application` and implemented by `PaystackPaymentGateway` in `DrinksEcommerce.Infrastructure/Payments/Paystack/PaystackPaymentGateway.cs`.

Two operating modes are supported via `Payments:Provider`:
- **`Paystack`**: Communicates directly with `https://api.paystack.co/` (`POST /transaction/initialize`, `GET /transaction/verify/{reference}`, `POST /refund`) using a typed `HttpClient` authenticated with `Authorization: Bearer <SecretKey>`.
- **`Simulated`** (default for local development and automated CI when live/test Paystack keys are not configured): Generates a local interactive test checkout page at `/dev/paystack/checkout?reference=...` that simulates successful card payment, declined payment, or abandonment, signs a real HMAC-SHA512 `charge.success` / `charge.failed` webhook, and redirects to the configured callback URL.

## 2. Currency & Kobo Conversion

- Public API and database amounts are stored in **NGN (Naira)** (`decimal(18,2)`).
- `NigerianValueRules.ToKobo(decimal amountNgn)` converts NGN to integer kobo (`1 NGN = 100 kobo`), rejecting zero, negative numbers, sub-kobo precision (e.g. `10.555`), and integer overflow.
- Floating-point types (`float`/`double`) are never used for financial calculations.

## 3. Transaction Initialization & Verification Flow

1. During `POST /api/v1/checkout`, the backend persists the `Order`, `Payment` (`Initialized`), and `StockReservation` (`Active`) in SQL Server **before** calling `IPaymentGateway.InitializePaymentAsync`.
2. Paystack returns `authorization_url` and `access_code`, which are recorded in `PaymentAttempt`, transitioning `Payment.Status` to `Pending`.
3. After the customer completes checkout on Paystack, the browser or mobile app is redirected to the callback URL (`/payment/verify?reference=...`) and calls `GET /api/v1/payments/{reference}/verify`.
4. The backend **never** trusts browser query parameters. It calls `IPaymentGateway.VerifyPaymentAsync(reference)` and validates:
   - `ProviderStatus == "success"`
   - `Currency == "NGN"`
   - `AmountKobo == Payment.ExpectedAmountKobo`
   - `MetadataOrderId` (when present) matches `Order.Id`
5. Only when all checks pass does `Payment.Status` transition to `Successful`, `StockReservation` transition to `Committed` (deducting `OnHand` stock once), and `Order.Status` transition to `Confirmed`.

## 4. Webhook Security & Idempotency (`POST /api/v1/payments/paystack/webhook`)

- Preserves the exact raw request body using `Request.EnableBuffering()`.
- Computes `HMAC-SHA512` over the raw UTF-8 body using `Payments:SecretKey` and compares it against `x-paystack-signature` using `CryptographicOperations.FixedTimeEquals`.
- Invalid signatures are immediately rejected with `401 Unauthorized`.
- Valid events are deduplicated by `DedupKey` in `PaymentWebhookEvents` and persisted before processing. Duplicate deliveries are safely acknowledged without duplicating inventory or financial transitions.

## 5. Configuring Paystack Test Keys, Callback URLs, and Webhook URLs

1. Obtain your **Test Secret Key** (`sk_test_...`) and **Test Public Key** (`pk_test_...`) from the [Paystack Dashboard → Settings → API Keys & Webhooks](https://dashboard.paystack.com/#/settings/developer).
2. Set the following environment variables (or user-secrets in development):
   ```powershell
   $env:Payments__Provider = "Paystack"
   $env:Payments__SecretKey = "sk_test_your_key_here"
   $env:Payments__PublicKey = "pk_test_your_key_here"
   $env:Payments__WebCallbackUrl = "http://localhost:5173/payment/verify"
   $env:Payments__MobileCallbackUrl = "drinksecommerce://payment/verify"
   ```
3. For local webhook testing with Paystack Dashboard, expose port `5080` via a tunnel (e.g. devtunnel/ngrok) and set the Paystack Test Webhook URL to:
   `https://<your-tunnel-domain>/api/v1/payments/paystack/webhook`
