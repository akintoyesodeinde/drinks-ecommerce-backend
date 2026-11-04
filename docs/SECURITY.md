# DrinksEcommerce — Security Controls & Nigerian Data Privacy (NDPA)

## 1. Authentication & Session Security

- **ASP.NET Core Identity**: Password policy requires minimum 8 characters, uppercase, lowercase, digit, and non-alphanumeric character. Account lockout triggers after 5 failed attempts for 15 minutes.
- **Short-Lived JWT Access Tokens**: Access tokens expire after 15 minutes (`Jwt:AccessTokenMinutes`).
- **Rotating Refresh Tokens with Reuse Detection**: Refresh tokens are 48-byte cryptographically random values stored as SHA-256 hashes (`RefreshTokens.TokenHash`) grouped by `FamilyId`. Every `/api/v1/auth/refresh` call revokes the old token and issues a new one. Presenting an already-revoked token immediately revokes the entire token family.

## 2. Authorization & Resource Ownership

- Roles: `Customer`, `Administrator`, `WarehouseStaff`, `DeliveryStaff`.
- Policies:
  - `AdminOnly`: Product/category mutations, refunds, reconciliation, coupons, delivery zones, analytics, audit logs.
  - `WarehouseOrAdmin`: Inventory adjustments and order preparation (`Processing`, `ReadyForDispatch`).
  - `DeliveryOrAdmin`: Delivery dispatch, delivery completion, and exception recording.
- **Strict Ownership Validation**: Customer endpoints (`/api/v1/orders/{id}`, `/api/v1/orders/{id}/tracking`, `/api/v1/payments/{reference}`, `/api/v1/account/addresses/{id}`) verify `order.UserId == currentUser.UserId` and return `404 Not Found` if another customer attempts access.
- **No Manual Payment Status Mutation**: Staff endpoints do not expose any action to mark an order or payment paid manually; only verified Paystack transactions can confirm payment.

## 3. Input Validation, Uploads & Headers

- **Server-Side Price & Fee Authority**: Clients submit only `variantId`, `quantity`, `couponCode`, and address details. Unit prices, discounts, delivery fees, and kobo totals are computed exclusively on the server.
- **Safe Image Uploads**: `POST /api/v1/admin/products/{id}/images` enforces a 2 MB size limit, inspects file magic bytes (allowing only JPEG, PNG, and WebP), and generates a random filename (`{productId}_{guid}.ext`) to prevent path traversal or script execution.
- **Security Headers**: Every API and Nginx response includes `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, and `X-Correlation-Id`.
- **Log Redaction**: Passwords, JWTs, refresh tokens, Paystack secret keys, and raw card/payment secrets are never logged. Raw card details are never handled or stored by the application.

## 4. Nigerian Data Protection Act (NDPA) & Retention Considerations

- **Data Minimization**: Only customer contact details required for order fulfilment and delivery in Nigeria (name, email, normalized `+234` phone number, and delivery address) are collected.
- **Financial & Tax Retention**: In accordance with Nigerian tax and financial audit requirements, `Orders`, `OrderItems`, `Payments`, `Refunds`, `InventoryTransactions`, and `AuditLogs` are immutable and protected by `DeleteBehavior.Restrict`.
- **Alcohol Sales Policy**: Alcohol products (`IsAlcoholic = true`) are hidden from public catalog and blocked at cart/checkout while `Store:AlcoholSalesEnabled = false` (default) until merchant licensing, age-verification (18+), and delivery policies are formally approved.
