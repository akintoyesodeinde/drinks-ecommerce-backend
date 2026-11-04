# DrinksEcommerce — Database Design

## 1. Database Engine & Conventions

- **Engine**: Microsoft SQL Server 2022 (`Microsoft.EntityFrameworkCore.SqlServer` 10.0.13).
- **Monetary Precision**: All financial columns (`Price`, `CompareAtPrice`, `Subtotal`, `Discount`, `DeliveryFee`, `Tax`, `Total`, `Amount`, `RefundedAmount`, `BaseFee`, `FreeDeliveryThreshold`, `ExpectedAmount`, `ActualAmount`) use `decimal(18,2)`.
- **Timestamps**: All entity timestamps (`CreatedAt`, `UpdatedAt`, `ExpiresAt`, `ConfirmedAt`, `PaidAt`, `DispatchedAt`, `DeliveredAt`, `ProcessedAt`) use `DateTimeOffset` stored in UTC.
- **Concurrency Control**: `InventoryItem`, `Order`, and `Payment` maintain a `RowVersion` concurrency token (`IsConcurrencyToken()`).
- **Historical Preservation**: `Order`, `OrderItem`, `OrderStatusHistory`, `Payment`, `PaymentAttempt`, `PaymentStatusHistory`, `Refund`, `InventoryItem`, and `OrderDelivery` use `DeleteBehavior.Restrict` to prevent accidental cascading deletion of financial or operational history.

## 2. Core Tables & Constraints

| Table | Key Constraints & Indexes | Purpose |
|---|---|---|
| `AspNetUsers`, `AspNetRoles`, ... | Unique `Email`, `NormalizedUserName` | Identity users & roles (`Customer`, `Administrator`, `WarehouseStaff`, `DeliveryStaff`) |
| `RefreshTokens` | Unique `TokenHash`, Index `(UserId, FamilyId)` | SHA-256 hashed refresh tokens with family rotation & reuse revocation |
| `Categories` | Unique `Slug` | Beverage categories |
| `Products` | Unique `Slug`, Index `Brand`, FK `CategoryId` (Restrict) | Storefront products with `IsAlcoholic` and `IsFeatured` flags |
| `ProductVariants` | Unique `Sku`, FK `ProductId` | Pack sizes (`Single`, `Multipack`, `Carton`, `Crate`, `Sachet`, `Bundle`), units per pack, NGN price |
| `ProductImages`, `ProductAttributes` | Index `ProductId` | Product gallery images and specifications |
| `InventoryItems` | Unique `VariantId`, `RowVersion` concurrency token | Tracks `OnHand`, `Reserved`, and `LowStockThreshold` |
| `InventoryTransactions` | Index `VariantId`, Index `OrderId` | Immutable ledger of every stock movement (`InitialStock`, `Reservation`, `Sale`, `Restock`, `Damage`, etc.) |
| `StockReservations`, `StockReservationItems` | Unique `OrderId`, Index `(Status, ExpiresAt)` | Holds reserved stock for `PendingPayment` orders until payment or expiry |
| `CustomerAddresses` | Index `UserId` | Saved Nigerian delivery addresses with normalized `+234` phone numbers |
| `Carts`, `CartItems` | Unique `UserId` | Server-persisted customer shopping carts |
| `Coupons`, `Promotions` | Unique `Code` | Discount coupons and promotional banners |
| `DeliveryZones`, `DeliveryFeeRules`, `PickupLocations` | FK `DeliveryZoneId` | Configurable Nigerian state/city coverage zones, tiered fee rules, and pickup hubs |
| `Orders`, `OrderItems`, `OrderStatusHistories` | Unique `OrderNumber`, Unique `(UserId, IdempotencyKey)` | Immutable order snapshots,owned `Shipping*` address snapshot columns, and status timeline |
| `OrderDeliveries`, `DeliveryStatusHistories` | Unique `OrderId` | Rider assignment, dispatch, delivery confirmation, and delivery exceptions |
| `Payments`, `PaymentAttempts`, `PaymentStatusHistories` | Unique `Reference`, Unique `OrderId` | Payment state machine, kobo amount verification, and initialization attempts |
| `PaymentWebhookEvents` | Unique `DedupKey`, Index `(IsProcessed, CreatedAt)` | Durable webhook store for idempotent event processing |
| `Refunds` | Index `OrderId`, FK `PaymentId` (Restrict) | Partial and full refund tracking |
| `ReconciliationRecords` | Index `(Status, Type)` | Financial discrepancy & exception records |
| `AuditLogs`, `Notifications`, `ApplicationSettings` | Index `CreatedAt`, Index `(UserId, IsRead, CreatedAt)` | Privileged action audit trail and customer notifications |
