# DrinksEcommerce — Operations Runbook & Deployment Guide

## 1. Health & Readiness Endpoints

- **Liveness**: `GET /health/live` — returns `200 Healthy` when the ASP.NET Core process is responsive.
- **Readiness**: `GET /health/ready` — verifies SQL Server connectivity via `ApplicationDbContext`.

## 2. Background Jobs (`OperationsBackgroundWorker`)

`OperationsBackgroundWorker` runs every 30 seconds inside the API host:
1. **Expired Stock Reservation Cleanup**: Releases `Active` reservations whose `ExpiresAt <= UtcNow` when payment has not succeeded, returning reserved stock to `Available` and transitioning the unpaid order to `Cancelled` and payment to `Abandoned`.
2. **Durable Webhook Retry**: Retries any `PaymentWebhookEvents` where `IsProcessed == false` and `AttemptCount < 5`.
3. **Notification Email Dispatch**: Sends any queued `Notifications` where `EmailDispatched == false`.

## 3. Handling Payment & Reconciliation Exceptions

1. Navigate to **Admin Console → Reconciliation** (`/admin/reconciliation`) or call `GET /api/v1/admin/reconciliation?status=Open`.
2. Click **Run Reconciliation** (`POST /api/v1/admin/reconciliation/run`) to query Paystack for any local `Pending`/`Initialized` payments that succeeded at the gateway.
3. Review open exceptions by type:
   - `PendingPaymentSucceededAtGateway`: Automatically reconciled and confirmed if stock is available.
   - `PaidButStockUnavailable`: Customer paid after reservation expired and stock was depleted. Contact the customer to arrange a replacement pack or initiate a full refund via `/admin/refunds`.
   - `AmountMismatch` / `CurrencyMismatch`: Inspect the gateway transaction in the Paystack Dashboard before resolving with an audit note (`POST /api/v1/admin/reconciliation/{id}/resolve`).
   - `ReversedTransaction`: Do not dispatch unfulfilled orders; record resolution notes after financial review.
