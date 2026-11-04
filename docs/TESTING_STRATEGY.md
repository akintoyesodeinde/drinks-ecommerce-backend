# DrinksEcommerce — Testing Strategy

## 1. Test Pyramid

1. **Architecture Tests (`backend/tests/DrinksEcommerce.ArchitectureTests`)**:
   - Verifies Clean Architecture assembly dependency boundaries (`Domain` has zero outer dependencies; `Application` depends only on `Domain`; `Infrastructure` and `Persistence` do not depend on `API`).
2. **Backend Unit Tests (`backend/tests/DrinksEcommerce.UnitTests`)**:
   - Tests `NigerianValueRules.ToKobo` / `FromKobo` and precision/negative/overflow guards.
   - Tests Nigerian mobile number normalization (`0803...`, `+234...`, `234...`) and rejection of invalid numbers.
   - Tests `ProductVariant` pack-size invariants (`Carton`/`Crate`/`Multipack` minimum unit rules, SKU normalization).
   - Tests `Coupon.Evaluate` (percentage discounts, max discount cap, minimum subtotal, per-customer usage limits).
   - Tests `DeliveryZone` matching and tiered fee rules + free-delivery thresholds.
   - Tests `InventoryItem` reservations, commits, releases, manual adjustments, negative-stock prevention, and `RowVersion` concurrency token updates.
   - Tests `Order` and `Payment` explicit state machines and cumulative `Refund` limit enforcement.
   - Tests `PaystackPaymentGateway` HMAC-SHA512 constant-time webhook signature verification and deterministic deduplication keys.
3. **Backend Integration Tests (`backend/tests/DrinksEcommerce.IntegrationTests`)**:
   - Uses `WebApplicationFactory<Program>` (`DrinksApiFactory`) to exercise the full HTTP pipeline, authentication, RBAC policies, database seeding, catalog filtering (including alcohol gating), cart, coupon application, delivery zone estimation, idempotent checkout, signed Paystack webhooks, duplicate webhook idempotency, invalid webhook signature rejection, customer order ownership isolation, warehouse/delivery staff fulfilment transitions, and refund processing.
4. **Web Frontend Tests (`frontend/drinks-ecommerce-web`)**:
   - Vitest + React Testing Library covering catalog browsing, cart updates, checkout Nigerian phone/address validation, delivery fee serviceability display, payment pending/failure/backend-verified success states, customer order history, and role-aware admin navigation.
5. **Mobile App Tests (`mobile/drinks-ecommerce-mobile`)**:
   - Jest + React Native Testing Library covering catalog rendering, cart quantity updates, checkout validation, delivery serviceability, backend-confirmed payment verification states, order history, and formatting utilities.

## 2. Running All Tests

```powershell
# Backend (.NET 10)
dotnet test backend/DrinksEcommerce.sln --verbosity normal

# Web Storefront
cd frontend/drinks-ecommerce-web
npm test

# Mobile App
cd mobile/drinks-ecommerce-mobile
npm test
```
