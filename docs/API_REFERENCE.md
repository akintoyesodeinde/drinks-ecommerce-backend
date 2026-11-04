# DrinksEcommerce — API Reference (v1)

This document is the **binding contract** between the ASP.NET Core 10 backend, the React web storefront
(`frontend/drinks-ecommerce-web`) and the Expo mobile app (`mobile/drinks-ecommerce-mobile`).
Any change to an endpoint must be reflected here.

Interactive documentation is also available at runtime: `GET /swagger` (Development only) and
the OpenAPI document at `GET /openapi/v1.json`.

## 1. Conventions

| Item | Convention |
|---|---|
| Base path | `/api/v1` |
| Local API URL | `http://localhost:5080` (dotnet run and Docker) |
| JSON | camelCase property names; enums serialized as **strings** (e.g. `"PendingPayment"`) |
| Money | JSON numbers with 2 decimal places, **NGN (Naira)** — never kobo on the public API. e.g. `12500.00` |
| Timestamps | ISO‑8601 UTC (`2026-10-25T11:34:14Z`). Clients convert to `Africa/Lagos` for display |
| IDs | GUID strings |
| Auth | `Authorization: Bearer <accessToken>` (JWT, 15 min). Refresh with `/auth/refresh` |
| Pagination | query `page` (1‑based, default 1), `pageSize` (default 12, max 100) |
| Idempotency | `POST /checkout` **requires** header `Idempotency-Key: <uuid>`; reuse the same key when retrying the same submission |
| Correlation | Every response carries `X-Correlation-Id`; clients may send one |

### Paged response

```json
{ "items": [ ... ], "page": 1, "pageSize": 12, "totalCount": 57, "totalPages": 5 }
```

### Error response (RFC 7807 ProblemDetails)

```json
{
  "type": "https://httpstatuses.io/400",
  "title": "Validation failed",
  "status": 400,
  "detail": "One or more validation errors occurred.",
  "code": "validation_error",
  "errors": { "email": ["Email is required."] },
  "traceId": "00-...",
  "correlationId": "..."
}
```

| Status | `code` examples | Meaning |
|---|---|---|
| 400 | `validation_error` | Request failed validation (`errors` populated) |
| 401 | `unauthorized` | Missing/expired token → client should try `/auth/refresh` once |
| 403 | `forbidden` | Authenticated but not permitted |
| 404 | `not_found` | Resource missing **or not owned by caller** |
| 409 | `conflict`, `insufficient_stock`, `invalid_state_transition`, `idempotency_conflict` | Business rule conflict |
| 422 | `delivery_unavailable`, `coupon_invalid`, `cart_empty`, `product_unavailable` | Business rule rejected the request |
| 429 | `rate_limited` | Too many requests |
| 502/503 | `payment_gateway_error` | Upstream payment provider failure |

## 2. Enumerations

```
OrderStatus      = PendingPayment | Confirmed | Processing | ReadyForDispatch | OutForDelivery | Delivered
                 | DeliveryFailed | CancellationRequested | Cancelled | RefundPending | PartiallyRefunded | Refunded
PaymentStatus    = Initialized | Pending | Successful | Failed | Abandoned | Reversed
                 | RefundPending | PartiallyRefunded | Refunded
DeliveryStatus   = NotScheduled | AwaitingDispatch | Assigned | OutForDelivery | Delivered | FailedAttempt | Returned | Cancelled
DeliveryMethod   = Delivery | Pickup
PackType         = Single | Multipack | Carton | Crate | Sachet | Bundle
StockStatus      = InStock | LowStock | OutOfStock
RefundStatus     = Requested | Pending | Processing | Processed | Failed | NeedsAttention
DiscountType     = Percentage | FixedAmount
InventoryTransactionType = InitialStock | Restock | Adjustment | Damage | Correction | Reservation | ReservationRelease | Sale | Return
ReconciliationType = PendingPaymentSucceededAtGateway | AmountMismatch | CurrencyMismatch | GatewayPaymentWithoutOrder
                 | DuplicateReference | AmbiguousInitialization | RefundDiscrepancy | ReversedTransaction
                 | RepeatedVerificationFailure | PaidButStockUnavailable
ReconciliationStatus = Open | Resolved
Roles            = Customer | Administrator | WarehouseStaff | DeliveryStaff
```

### Order state machine

```
PendingPayment     -> Confirmed (verified payment) | Cancelled (customer, payment failed/abandoned, reservation expired)
Confirmed          -> Processing | CancellationRequested | RefundPending
Processing         -> ReadyForDispatch | CancellationRequested | RefundPending
ReadyForDispatch   -> OutForDelivery | Delivered (pickup collected) | CancellationRequested
OutForDelivery     -> Delivered | DeliveryFailed
DeliveryFailed     -> OutForDelivery (re-attempt) | ReadyForDispatch | RefundPending | Cancelled
CancellationRequested -> Cancelled (approved, unpaid) | RefundPending (approved, paid) | <previous status> (rejected)
RefundPending      -> PartiallyRefunded | Refunded | <previous status> (refund failed)
PartiallyRefunded  -> RefundPending | Refunded
```

## 3. Authentication & account

| Method | Path | Auth | Body | Response |
|---|---|---|---|---|
| POST | `/auth/register` | – | `RegisterRequest` | 201 `AuthResponse` |
| POST | `/auth/login` | – | `{ email, password }` | 200 `AuthResponse` |
| POST | `/auth/refresh` | – | `{ refreshToken }` | 200 `AuthResponse` (old refresh token is revoked; reuse of a revoked token revokes the whole session family) |
| POST | `/auth/logout` | – | `{ refreshToken }` | 204 |
| POST | `/auth/forgot-password` | – | `{ email }` | 202 (always, no user enumeration) |
| POST | `/auth/reset-password` | – | `{ email, token, newPassword }` | 204 |
| POST | `/auth/confirm-email` | – | `{ userId, token }` | 204 |
| POST | `/auth/resend-confirmation` | Bearer | – | 202 |
| GET | `/account/profile` | Bearer | – | `UserDto` |
| PUT | `/account/profile` | Bearer | `{ firstName, lastName, phoneNumber }` | `UserDto` |
| POST | `/account/change-password` | Bearer | `{ currentPassword, newPassword }` | 204 |

```ts
RegisterRequest = { firstName: string; lastName: string; email: string; phoneNumber: string; password: string }
// password: min 8 chars, upper, lower, digit, non-alphanumeric
// phoneNumber: Nigerian format, accepts 0803xxxxxxx, +234803xxxxxxx, 234803xxxxxxx; stored normalised as +234XXXXXXXXXX
AuthResponse = { accessToken: string; accessTokenExpiresAt: string; refreshToken: string; refreshTokenExpiresAt: string; user: UserDto }
UserDto = { id: string; email: string; firstName: string; lastName: string; phoneNumber: string | null; emailConfirmed: boolean; roles: string[] }
```

## 4. Addresses (Bearer)

| Method | Path | Body | Response |
|---|---|---|---|
| GET | `/account/addresses` | – | `AddressDto[]` |
| POST | `/account/addresses` | `AddressInput` | 201 `AddressDto` |
| PUT | `/account/addresses/{id}` | `AddressInput` | `AddressDto` |
| DELETE | `/account/addresses/{id}` | – | 204 |

```ts
AddressInput = { label?: string; recipientName: string; phoneNumber: string; state: string; city: string;
                 street: string; landmark?: string; postalCode?: string; deliveryInstructions?: string; isDefault?: boolean }
AddressDto   = AddressInput & { id: string; isDefault: boolean; isServiceable: boolean }
```

## 5. Catalog (public)

| Method | Path | Query | Response |
|---|---|---|---|
| GET | `/products` | `search, categoryId, categorySlug, brand, minPrice, maxPrice, inStockOnly, featured, sort, page, pageSize` | `Paged<ProductSummaryDto>` |
| GET | `/products/{idOrSlug}` | – | `ProductDetailDto` |
| GET | `/categories` | – | `CategoryDto[]` |
| GET | `/categories/{idOrSlug}` | – | `CategoryDto` |
| GET | `/categories/{idOrSlug}/products` | `sort, page, pageSize, search` | `Paged<ProductSummaryDto>` |
| GET | `/brands` | – | `string[]` |
| GET | `/promotions/active` | – | `PromotionDto[]` |

`sort` = `relevance` (default) | `name` | `price_asc` | `price_desc` | `newest`

```ts
VariantDto = { id: string; sku: string; name: string;              // e.g. "Carton (12 x 50cl)"
               packType: PackType; unitsPerPack: number; unitVolumeMl: number | null;
               price: number; compareAtPrice: number | null;
               stockStatus: StockStatus; availableQuantity: number; // capped at 99 for display
               isActive: boolean }
ProductSummaryDto = { id: string; slug: string; name: string; brand: string; categoryId: string; categoryName: string; categorySlug: string;
                      imageUrl: string | null; minPrice: number; maxPrice: number; inStock: boolean; isAlcoholic: boolean;
                      isFeatured: boolean; variantCount: number; defaultVariant: VariantDto | null }
ProductDetailDto  = { id; slug; name; description: string; brand; categoryId; categoryName; categorySlug; isAlcoholic; isFeatured;
                      images: { id: string; url: string; altText: string | null; sortOrder: number }[];
                      attributes: { name: string; value: string }[];
                      variants: VariantDto[] }
CategoryDto  = { id: string; name: string; slug: string; description: string | null; imageUrl: string | null; productCount: number }
PromotionDto = { id: string; title: string; description: string; imageUrl: string | null; linkUrl: string | null; startsAt: string; endsAt: string | null }
```

Image URLs are **relative** to the API origin (e.g. `/images/products/coke-50cl.svg`, `/uploads/products/...`).
Clients prefix them with the API base origin (web uses same-origin proxy, so relative paths work directly).

Alcoholic products are hidden from the public catalog while `Store:AlcoholSalesEnabled=false` (default).

## 6. Cart (Bearer — guests keep a local cart and call `/cart/merge` after login)

| Method | Path | Body | Response |
|---|---|---|---|
| GET | `/cart` | – | `CartDto` |
| POST | `/cart/items` | `{ variantId, quantity }` (adds to existing line) | `CartDto` |
| PUT | `/cart/items/{itemId}` | `{ quantity }` (1–99) | `CartDto` |
| DELETE | `/cart/items/{itemId}` | – | `CartDto` |
| DELETE | `/cart` | – | 204 |
| POST | `/cart/coupon` | `{ code }` | `CartDto` (422 `coupon_invalid` with reason) |
| DELETE | `/cart/coupon` | – | `CartDto` |
| POST | `/cart/merge` | `{ items: { variantId, quantity }[] }` | `CartDto` |

```ts
CartItemDto = { id: string; productId: string; productName: string; productSlug: string; variantId: string; variantName: string;
                sku: string; imageUrl: string | null; unitPrice: number; quantity: number; lineTotal: number;
                availableQuantity: number; stockStatus: StockStatus; isAvailable: boolean }
CartDto = { id: string; items: CartItemDto[]; itemCount: number; subtotal: number; discount: number; couponCode: string | null;
            total: number;            // subtotal - discount (delivery excluded)
            warnings: string[] }      // e.g. "Fanta 50cl: only 3 left, quantity adjusted"
```

All prices are recomputed server-side from the current catalog on every read.

## 7. Delivery (public)

| Method | Path | Query | Response |
|---|---|---|---|
| GET | `/delivery/zones` | – | `DeliveryZoneDto[]` |
| GET | `/delivery/estimate` | `state, city, subtotal` | `DeliveryEstimateDto` |
| GET | `/delivery/pickup-locations` | – | `PickupLocationDto[]` (empty when pickup disabled) |
| GET | `/delivery/states` | – | `string[]` (all 36 states + FCT) |

```ts
DeliveryZoneDto = { id: string; name: string; state: string; cities: string[]; baseFee: number; freeDeliveryThreshold: number | null;
                    estimatedMinDays: number; estimatedMaxDays: number }
DeliveryEstimateDto = { serviceable: boolean; zoneId: string | null; zoneName: string | null; fee: number; freeDeliveryApplied: boolean;
                        freeDeliveryThreshold: number | null; estimatedMinDays: number | null; estimatedMaxDays: number | null; message: string }
PickupLocationDto = { id: string; name: string; address: string; state: string; city: string; openingHours: string; phoneNumber: string }
```

## 8. Checkout (Bearer)

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/checkout/preview` | `CheckoutRequest` (no idempotency key needed) | `CheckoutPreviewDto` |
| POST | `/checkout` | `CheckoutRequest` + header `Idempotency-Key` | 201 `CheckoutResultDto` |

```ts
CheckoutRequest = {
  deliveryMethod: "Delivery" | "Pickup";
  addressId?: string;              // saved address, OR
  address?: AddressInput;          // a new address (saveAddress=true stores it)
  saveAddress?: boolean;
  pickupLocationId?: string;       // required when Pickup
  deliveryInstructions?: string;
  contactEmail?: string;           // defaults to account email
  client?: "web" | "mobile";       // selects the configured Paystack callback URL (default "web")
}
CheckoutPreviewDto = { items: CartItemDto[]; subtotal: number; discount: number; deliveryFee: number; tax: number; total: number;
                       currency: "NGN"; couponCode: string | null; serviceable: boolean; message: string | null;
                       estimatedMinDays: number | null; estimatedMaxDays: number | null; warnings: string[] }
CheckoutResultDto  = { orderId: string; orderNumber: string; total: number; currency: "NGN";
                       paymentReference: string; authorizationUrl: string | null;   // null when initialization was ambiguous
                       paymentStatus: PaymentStatus; reservationExpiresAt: string; message: string | null }
```

Client flow: open `authorizationUrl` (web: `window.location.assign`; mobile: `WebBrowser.openAuthSessionAsync`).
Paystack redirects to the callback URL with `?reference=...&trxref=...`. The client then calls
`GET /payments/{reference}/verify` and shows **only** the backend-confirmed status.
Repeating `POST /checkout` with the same `Idempotency-Key` returns the same order (200) instead of creating a new one.

## 9. Payments

| Method | Path | Auth | Body | Response |
|---|---|---|---|---|
| POST | `/payments/initialize` | Bearer | `{ orderId, client?: "web"\|"mobile" }` | `CheckoutResultDto` — resumes/re-initializes payment for a `PendingPayment` order |
| GET | `/payments/{reference}/verify` | Bearer (owner) | – | `PaymentStatusDto` (server verifies with Paystack and applies the result idempotently) |
| GET | `/payments/{reference}` | Bearer (owner) | – | `PaymentStatusDto` (local state only) |
| POST | `/payments/paystack/webhook` | Paystack signature | raw Paystack event | 200 when stored/duplicate, 401 on invalid signature |

```ts
PaymentStatusDto = { reference: string; orderId: string; orderNumber: string; status: PaymentStatus; amount: number; currency: "NGN";
                     paidAt: string | null; channel: string | null; orderStatus: OrderStatus; message: string }
```

Development without Paystack keys: `Payments:Provider=Simulated` makes `authorizationUrl` point at
`/dev/paystack/checkout?reference=...` on the API — a test page that simulates success/failure/abandonment,
sends a **signed** webhook to the API and redirects to the callback URL. It is disabled outside Development.

## 10. Orders & tracking (Bearer, owner only)

| Method | Path | Query/Body | Response |
|---|---|---|---|
| GET | `/orders` | `status, page, pageSize` | `Paged<OrderSummaryDto>` |
| GET | `/orders/{id}` | – | `OrderDetailDto` |
| POST | `/orders/{id}/cancel` | `{ reason?: string }` | `OrderDetailDto` (PendingPayment → Cancelled; Confirmed/Processing → CancellationRequested) |
| GET | `/orders/{id}/tracking` | – | `TrackingDto` |

```ts
OrderSummaryDto = { id: string; orderNumber: string; status: OrderStatus; paymentStatus: PaymentStatus | null; deliveryMethod: DeliveryMethod;
                    total: number; currency: "NGN"; itemCount: number; createdAt: string }
OrderItemDto = { id: string; productId: string; variantId: string; productName: string; variantName: string; sku: string;
                 imageUrl: string | null; unitPrice: number; quantity: number; lineTotal: number }
OrderAddressDto = { recipientName: string; phoneNumber: string; state: string; city: string; street: string; landmark: string | null; postalCode: string | null }
StatusHistoryDto = { status: string; note: string | null; actor: string | null; createdAt: string }
OrderDetailDto = { id; orderNumber; status: OrderStatus; paymentStatus: PaymentStatus | null; deliveryStatus: DeliveryStatus | null;
                   deliveryMethod: DeliveryMethod; items: OrderItemDto[]; subtotal: number; discount: number; deliveryFee: number; tax: number;
                   total: number; currency: "NGN"; couponCode: string | null; address: OrderAddressDto | null;
                   pickupLocation: PickupLocationDto | null; deliveryInstructions: string | null; contactEmail: string;
                   payment: { reference: string; status: PaymentStatus; amount: number; paidAt: string | null; channel: string | null } | null;
                   statusHistory: StatusHistoryDto[]; canCancel: boolean; canPay: boolean;
                   reservationExpiresAt: string | null; createdAt: string }
TrackingDto = { orderId: string; orderNumber: string; orderStatus: OrderStatus; deliveryStatus: DeliveryStatus | null; deliveryMethod: DeliveryMethod;
                estimatedDeliveryFrom: string | null; estimatedDeliveryTo: string | null;
                rider: { name: string; phoneNumber: string } | null;
                steps: { key: string; label: string; completed: boolean; current: boolean; occurredAt: string | null }[];
                history: StatusHistoryDto[] }
```

Tracking `steps` keys (in order): `placed`, `paid`, `processing`, `ready`, `out_for_delivery` (omitted for Pickup), `delivered`.

## 11. Notifications (Bearer)

| Method | Path | Response |
|---|---|---|
| GET | `/notifications?page&pageSize&unreadOnly` | `Paged<NotificationDto>` |
| GET | `/notifications/unread-count` | `{ count: number }` |
| POST | `/notifications/{id}/read` | 204 |
| POST | `/notifications/read-all` | 204 |

```ts
NotificationDto = { id: string; type: string; title: string; message: string; orderId: string | null; isRead: boolean; createdAt: string }
```

## 12. Administration

Role policies: **Administrator** (everything), **WarehouseStaff** (catalog read, inventory, order fulfilment up to ReadyForDispatch),
**DeliveryStaff** (deliveries assigned to them and unassigned ready orders). Payment status can never be set manually.

### Products — `Administrator` (write), `WarehouseStaff` (read)

| Method | Path | Body | Response |
|---|---|---|---|
| GET | `/admin/products?search&categoryId&includeInactive&page&pageSize` | – | `Paged<AdminProductSummaryDto>` |
| GET | `/admin/products/{id}` | – | `AdminProductDto` |
| POST | `/admin/products` | `CreateProductRequest` | 201 `AdminProductDto` |
| PUT | `/admin/products/{id}` | `UpdateProductRequest` | `AdminProductDto` |
| DELETE | `/admin/products/{id}` | – | 204 (deactivates; history preserved) |
| POST | `/admin/products/{id}/variants` | `VariantInput` | `AdminProductDto` |
| PUT | `/admin/products/{id}/variants/{variantId}` | `VariantInput` | `AdminProductDto` |
| POST | `/admin/products/{id}/images` | `multipart/form-data` (`file`, `altText`) JPEG/PNG/WebP ≤ 2 MB | `AdminProductDto` |
| DELETE | `/admin/products/{id}/images/{imageId}` | – | `AdminProductDto` |

```ts
VariantInput = { sku: string; name: string; packType: PackType; unitsPerPack: number; unitVolumeMl?: number | null; price: number;
                 compareAtPrice?: number | null; isActive: boolean; lowStockThreshold: number; initialStock?: number /* create only */ }
CreateProductRequest = { name; description; brand; categoryId; isAlcoholic: boolean; isFeatured: boolean; isActive: boolean;
                         attributes: { name: string; value: string }[]; variants: VariantInput[] }
UpdateProductRequest = Omit<CreateProductRequest, "variants">
AdminVariantDto = VariantDto & { onHand: number; reserved: number; available: number; lowStockThreshold: number }
AdminProductSummaryDto = { id; slug; name; brand; categoryName; imageUrl; isActive; isAlcoholic; isFeatured; variantCount; minPrice; maxPrice; totalAvailable: number; updatedAt: string }
AdminProductDto = Omit<ProductDetailDto, "variants"> & { isActive: boolean; createdAt: string; updatedAt: string; variants: AdminVariantDto[] }
```

### Categories — `Administrator`

`GET /admin/categories` → `AdminCategoryDto[]` · `POST /admin/categories` `{ name, description, isActive, sortOrder }` → 201 ·
`PUT /admin/categories/{id}` (same body) · `DELETE /admin/categories/{id}` → 204 (409 if it still has products)

```ts
AdminCategoryDto = CategoryDto & { isActive: boolean; sortOrder: number }
```

### Inventory — `Administrator`, `WarehouseStaff`

| Method | Path | Body | Response |
|---|---|---|---|
| GET | `/admin/inventory?search&lowStockOnly&page&pageSize` | – | `Paged<InventoryItemDto>` |
| POST | `/admin/inventory/{variantId}/adjust` | `{ quantityChange: number; type: "Restock"\|"Adjustment"\|"Damage"\|"Correction"; reason: string }` | `InventoryItemDto` (409 `insufficient_stock` if on‑hand would drop below reserved) |
| PUT | `/admin/inventory/{variantId}/threshold` | `{ lowStockThreshold }` | `InventoryItemDto` |
| GET | `/admin/inventory/{variantId}/transactions?page&pageSize` | – | `Paged<InventoryTransactionDto>` |

```ts
InventoryItemDto = { variantId; productId; productName; variantName; sku; onHand: number; reserved: number; available: number;
                     lowStockThreshold: number; isLowStock: boolean; updatedAt: string }
InventoryTransactionDto = { id; type: InventoryTransactionType; quantityChange: number; onHandAfter: number; reservedAfter: number;
                            reason: string | null; orderId: string | null; actor: string | null; createdAt: string }
```

### Orders — `Administrator`, `WarehouseStaff`

| Method | Path | Body | Response |
|---|---|---|---|
| GET | `/admin/orders?status&search&from&to&page&pageSize` | – | `Paged<AdminOrderSummaryDto>` |
| GET | `/admin/orders/{id}` | – | `AdminOrderDetailDto` |
| POST | `/admin/orders/{id}/status` | `{ status: "Processing"\|"ReadyForDispatch"\|"Delivered"; note?: string }` (`Delivered` only for Pickup) | `AdminOrderDetailDto` |
| POST | `/admin/orders/{id}/cancellation/approve` | `{ note?: string }` | `AdminOrderDetailDto` (Administrator) |
| POST | `/admin/orders/{id}/cancellation/reject` | `{ note?: string }` | `AdminOrderDetailDto` (Administrator) |

```ts
AdminOrderSummaryDto = OrderSummaryDto & { customerName: string; customerEmail: string }
AdminOrderDetailDto = OrderDetailDto & { customerId: string; customerName: string; customerEmail: string; customerPhone: string | null;
                                         delivery: DeliveryDto | null; allowedActions: string[];   // e.g. ["Processing","CancelApprove"]
                                         refundedAmount: number; refundableAmount: number }
```

### Deliveries — `Administrator`, `DeliveryStaff`

| Method | Path | Body | Response |
|---|---|---|---|
| GET | `/admin/deliveries?status&page&pageSize` | – | `Paged<DeliveryDto>` |
| GET | `/admin/deliveries/staff` | – | `{ id; name; email; phoneNumber }[]` (Administrator) |
| POST | `/admin/deliveries/{orderId}/assign` | `{ assignedToUserId?: string; riderName: string; riderPhone: string; scheduledFor?: string }` | `DeliveryDto` (Administrator) |
| POST | `/admin/deliveries/{orderId}/dispatch` | `{ note?: string }` | `DeliveryDto` |
| POST | `/admin/deliveries/{orderId}/deliver` | `{ receivedBy?: string; note?: string }` | `DeliveryDto` |
| POST | `/admin/deliveries/{orderId}/exception` | `{ reason: "CustomerUnavailable"\|"AddressIssue"\|"Damaged"\|"Refused"\|"Other"; note?: string }` | `DeliveryDto` |

```ts
DeliveryDto = { id; orderId; orderNumber; status: DeliveryStatus; orderStatus: OrderStatus; recipientName; phoneNumber; addressLine: string;
                state; city; deliveryInstructions: string | null; assignedToUserId: string | null; riderName: string | null; riderPhone: string | null;
                scheduledFor: string | null; dispatchedAt: string | null; deliveredAt: string | null; receivedBy: string | null;
                attemptCount: number; lastException: string | null; updatedAt: string; history: StatusHistoryDto[] }
```

### Payments, refunds, reconciliation — `Administrator`

| Method | Path | Body | Response |
|---|---|---|---|
| GET | `/admin/payments?status&search&page&pageSize` | – | `Paged<AdminPaymentDto>` |
| GET | `/admin/payments/{id}` | – | `AdminPaymentDetailDto` |
| POST | `/admin/payments/{reference}/reverify` | – | `PaymentStatusDto` |
| GET | `/admin/refunds?status&page&pageSize` | – | `Paged<RefundDto>` |
| POST | `/admin/refunds` | `{ orderId: string; amount: number; reason: string }` | 201 `RefundDto` |
| POST | `/admin/refunds/{id}/sync` | – | `RefundDto` (fetches provider status) |
| GET | `/admin/reconciliation?status&type&page&pageSize` | – | `Paged<ReconciliationRecordDto>` |
| POST | `/admin/reconciliation/run` | – | `{ paymentsChecked: number; exceptionsCreated: number }` |
| POST | `/admin/reconciliation/{id}/resolve` | `{ note: string }` | `ReconciliationRecordDto` |

```ts
AdminPaymentDto = { id; reference; orderId; orderNumber; status: PaymentStatus; amount; currency; channel: string | null; paidAt: string | null;
                    createdAt: string; refundedAmount: number; customerEmail: string; attemptCount: number }
AdminPaymentDetailDto = AdminPaymentDto & { gatewayResponse: string | null; providerTransactionId: string | null;
                    attempts: { id; reference; status: string; authorizationUrl: string | null; errorMessage: string | null; createdAt: string }[];
                    refunds: RefundDto[]; history: StatusHistoryDto[] }
RefundDto = { id; paymentId; paymentReference; orderId; orderNumber; amount; currency; status: RefundStatus; reason; providerRefundId: string | null;
              requestedBy: string | null; failureReason: string | null; createdAt: string; processedAt: string | null }
ReconciliationRecordDto = { id; type: ReconciliationType; status: ReconciliationStatus; paymentReference: string | null; orderId: string | null;
              orderNumber: string | null; expectedAmount: number | null; actualAmount: number | null; currency: string | null; description: string;
              createdAt: string; resolvedAt: string | null; resolvedBy: string | null; resolutionNote: string | null }
```

### Coupons, delivery zones, analytics, audit — `Administrator`

| Method | Path | Body / Query | Response |
|---|---|---|---|
| GET/POST | `/admin/coupons` | `CouponInput` | `CouponDto[]` / 201 `CouponDto` |
| PUT | `/admin/coupons/{id}` | `CouponInput` | `CouponDto` |
| GET/POST | `/admin/delivery-zones` | `DeliveryZoneInput` | `AdminDeliveryZoneDto[]` / 201 |
| PUT | `/admin/delivery-zones/{id}` | `DeliveryZoneInput` | `AdminDeliveryZoneDto` |
| GET | `/admin/analytics/summary?from&to` | dates `yyyy-MM-dd` (Africa/Lagos) | `SalesSummaryDto` |
| GET | `/admin/audit-logs?page&pageSize&entityType` | – | `Paged<AuditLogDto>` |

```ts
CouponInput = { code; description?: string; discountType: DiscountType; value: number; minOrderAmount?: number | null; maxDiscountAmount?: number | null;
                startsAt?: string | null; endsAt?: string | null; usageLimit?: number | null; perCustomerLimit?: number | null; isActive: boolean }
CouponDto = CouponInput & { id: string; usedCount: number }
DeliveryZoneInput = { name; state; cities: string[]; baseFee: number; freeDeliveryThreshold?: number | null; estimatedMinDays: number;
                      estimatedMaxDays: number; isActive: boolean; feeRules: { minOrderAmount: number; maxOrderAmount: number | null; fee: number }[] }
AdminDeliveryZoneDto = DeliveryZoneInput & { id: string }
SalesSummaryDto = { from: string; to: string; grossRevenue: number; refundedAmount: number; netRevenue: number; paidOrders: number;
                    averageOrderValue: number; pendingPaymentOrders: number; ordersByStatus: { status: string; count: number }[];
                    topProducts: { productName: string; variantName: string; sku: string; quantity: number; revenue: number }[];
                    dailySales: { date: string; orders: number; revenue: number }[]; lowStockCount: number; openReconciliationCount: number }
AuditLogDto = { id; actor: string | null; action: string; entityType: string; entityId: string | null; details: string | null; ipAddress: string | null; createdAt: string }
```

## 13. Health

| Path | Purpose |
|---|---|
| `GET /health/live` | Process liveness (no dependencies) |
| `GET /health/ready` | SQL Server (+ Redis when configured) readiness |

## 14. Development seed accounts (Development environment only)

| Role | Email | Password |
|---|---|---|
| Administrator | `admin@drinksecommerce.local` | `Admin#12345` |
| WarehouseStaff | `warehouse@drinksecommerce.local` | `Warehouse#12345` |
| DeliveryStaff | `rider@drinksecommerce.local` | `Rider#12345` |
| Customer | `customer@drinksecommerce.local` | `Customer#12345` |

Development coupon: `WELCOME10` (10 % off, min ₦5,000, max discount ₦2,000).
