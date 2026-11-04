using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Domain.Customers;
using DrinksEcommerce.Domain.Delivery;
using DrinksEcommerce.Domain.Inventory;
using DrinksEcommerce.Domain.Operations;
using DrinksEcommerce.Domain.Orders;
using DrinksEcommerce.Domain.Payments;
using DrinksEcommerce.Domain.Products;
using DrinksEcommerce.Persistence.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace DrinksEcommerce.Persistence;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>(options), IApplicationDbContext
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
    public DbSet<ProductImage> ProductImages => Set<ProductImage>();
    public DbSet<ProductAttribute> ProductAttributes => Set<ProductAttribute>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<InventoryTransaction> InventoryTransactions => Set<InventoryTransaction>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();
    public DbSet<StockReservationItem> StockReservationItems => Set<StockReservationItem>();
    public DbSet<CustomerAddress> CustomerAddresses => Set<CustomerAddress>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Cart> Carts => Set<Cart>();
    public DbSet<CartItem> CartItems => Set<CartItem>();
    public DbSet<Coupon> Coupons => Set<Coupon>();
    public DbSet<Promotion> Promotions => Set<Promotion>();
    public DbSet<DeliveryZone> DeliveryZones => Set<DeliveryZone>();
    public DbSet<DeliveryFeeRule> DeliveryFeeRules => Set<DeliveryFeeRule>();
    public DbSet<PickupLocation> PickupLocations => Set<PickupLocation>();
    public DbSet<OrderDelivery> OrderDeliveries => Set<OrderDelivery>();
    public DbSet<DeliveryStatusHistory> DeliveryStatusHistories => Set<DeliveryStatusHistory>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderStatusHistory> OrderStatusHistories => Set<OrderStatusHistory>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<PaymentAttempt> PaymentAttempts => Set<PaymentAttempt>();
    public DbSet<PaymentStatusHistory> PaymentStatusHistories => Set<PaymentStatusHistory>();
    public DbSet<PaymentWebhookEvent> PaymentWebhookEvents => Set<PaymentWebhookEvent>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<ReconciliationRecord> ReconciliationRecords => Set<ReconciliationRecord>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<ApplicationSetting> ApplicationSettings => Set<ApplicationSetting>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Category>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(120).IsRequired();
            b.Property(x => x.Slug).HasMaxLength(140).IsRequired();
            b.Property(x => x.Description).HasMaxLength(600);
            b.Property(x => x.ImageUrl).HasMaxLength(500);
            b.HasIndex(x => x.Slug).IsUnique();
        });

        builder.Entity<Product>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Slug).HasMaxLength(220).IsRequired();
            b.Property(x => x.Brand).HasMaxLength(120).IsRequired();
            b.Property(x => x.Description).HasMaxLength(2000);
            b.HasIndex(x => x.Slug).IsUnique();
            b.HasIndex(x => x.Brand);
            b.HasOne(x => x.Category)
                .WithMany(c => c.Products)
                .HasForeignKey(x => x.CategoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<ProductVariant>(b =>
        {
            b.Property(x => x.Sku).HasMaxLength(64).IsRequired();
            b.Property(x => x.Name).HasMaxLength(160).IsRequired();
            b.Property(x => x.PackType).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Price).HasPrecision(18, 2);
            b.Property(x => x.CompareAtPrice).HasPrecision(18, 2);
            b.HasIndex(x => x.Sku).IsUnique();
            b.HasOne(x => x.Product)
                .WithMany(p => p.Variants)
                .HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ProductImage>(b =>
        {
            b.Property(x => x.Url).HasMaxLength(500).IsRequired();
            b.Property(x => x.AltText).HasMaxLength(200);
            b.HasIndex(x => x.ProductId);
        });

        builder.Entity<ProductAttribute>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(100).IsRequired();
            b.Property(x => x.Value).HasMaxLength(300).IsRequired();
            b.HasIndex(x => x.ProductId);
        });

        builder.Entity<InventoryItem>(b =>
        {
            b.HasIndex(x => x.VariantId).IsUnique();
            b.Property(x => x.RowVersion).IsConcurrencyToken();
            b.HasOne(x => x.Variant)
                .WithOne(v => v.Inventory)
                .HasForeignKey<InventoryItem>(x => x.VariantId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<InventoryTransaction>(b =>
        {
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Reason).HasMaxLength(500);
            b.Property(x => x.Actor).HasMaxLength(200);
            b.HasIndex(x => x.VariantId);
            b.HasIndex(x => x.OrderId);
        });

        builder.Entity<StockReservation>(b =>
        {
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            b.HasIndex(x => x.OrderId).IsUnique();
            b.HasIndex(x => new { x.Status, x.ExpiresAt });
            b.HasMany(x => x.Items)
                .WithOne()
                .HasForeignKey(i => i.ReservationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<CustomerAddress>(b =>
        {
            b.Property(x => x.Label).HasMaxLength(60);
            b.Property(x => x.RecipientName).HasMaxLength(160).IsRequired();
            b.Property(x => x.PhoneNumber).HasMaxLength(32).IsRequired();
            b.Property(x => x.State).HasMaxLength(80).IsRequired();
            b.Property(x => x.City).HasMaxLength(120).IsRequired();
            b.Property(x => x.Street).HasMaxLength(300).IsRequired();
            b.Property(x => x.Landmark).HasMaxLength(200);
            b.Property(x => x.PostalCode).HasMaxLength(32);
            b.Property(x => x.DeliveryInstructions).HasMaxLength(500);
            b.HasIndex(x => x.UserId);
        });

        builder.Entity<RefreshToken>(b =>
        {
            b.Property(x => x.TokenHash).HasMaxLength(128).IsRequired();
            b.Property(x => x.ReplacedByTokenHash).HasMaxLength(128);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => new { x.UserId, x.FamilyId });
        });

        builder.Entity<Cart>(b =>
        {
            b.Property(x => x.CouponCode).HasMaxLength(64);
            b.HasIndex(x => x.UserId).IsUnique();
            b.HasMany(x => x.Items)
                .WithOne()
                .HasForeignKey(i => i.CartId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<Coupon>(b =>
        {
            b.Property(x => x.Code).HasMaxLength(64).IsRequired();
            b.Property(x => x.Description).HasMaxLength(400);
            b.Property(x => x.DiscountType).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.Value).HasPrecision(18, 2);
            b.Property(x => x.MinOrderAmount).HasPrecision(18, 2);
            b.Property(x => x.MaxDiscountAmount).HasPrecision(18, 2);
            b.HasIndex(x => x.Code).IsUnique();
        });

        builder.Entity<Promotion>(b =>
        {
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
            b.Property(x => x.Description).HasMaxLength(600).IsRequired();
            b.Property(x => x.ImageUrl).HasMaxLength(500);
            b.Property(x => x.LinkUrl).HasMaxLength(500);
        });

        builder.Entity<DeliveryZone>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(160).IsRequired();
            b.Property(x => x.State).HasMaxLength(80).IsRequired();
            b.Property(x => x.CitiesCsv).HasMaxLength(1000);
            b.Property(x => x.BaseFee).HasPrecision(18, 2);
            b.Property(x => x.FreeDeliveryThreshold).HasPrecision(18, 2);
            b.HasMany(x => x.FeeRules)
                .WithOne()
                .HasForeignKey(r => r.DeliveryZoneId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DeliveryFeeRule>(b =>
        {
            b.Property(x => x.MinOrderAmount).HasPrecision(18, 2);
            b.Property(x => x.MaxOrderAmount).HasPrecision(18, 2);
            b.Property(x => x.Fee).HasPrecision(18, 2);
        });

        builder.Entity<PickupLocation>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(160).IsRequired();
            b.Property(x => x.Address).HasMaxLength(300).IsRequired();
            b.Property(x => x.State).HasMaxLength(80).IsRequired();
            b.Property(x => x.City).HasMaxLength(120).IsRequired();
            b.Property(x => x.OpeningHours).HasMaxLength(160).IsRequired();
            b.Property(x => x.PhoneNumber).HasMaxLength(32).IsRequired();
        });

        builder.Entity<Order>(b =>
        {
            b.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
            b.Property(x => x.ContactEmail).HasMaxLength(200).IsRequired();
            b.Property(x => x.CustomerName).HasMaxLength(160).IsRequired();
            b.Property(x => x.CustomerPhone).HasMaxLength(32);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.PreviousStatusBeforeHold).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.DeliveryMethod).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.CouponCode).HasMaxLength(64);
            b.Property(x => x.Subtotal).HasPrecision(18, 2);
            b.Property(x => x.Discount).HasPrecision(18, 2);
            b.Property(x => x.DeliveryFee).HasPrecision(18, 2);
            b.Property(x => x.Tax).HasPrecision(18, 2);
            b.Property(x => x.Total).HasPrecision(18, 2);
            b.Property(x => x.Currency).HasMaxLength(8).IsRequired();
            b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
            b.Property(x => x.DeliveryInstructions).HasMaxLength(500);
            b.Property(x => x.RowVersion).IsConcurrencyToken();

            b.HasIndex(x => x.OrderNumber).IsUnique();
            b.HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();
            b.HasIndex(x => new { x.UserId, x.CreatedAt });

            b.OwnsOne(x => x.AddressSnapshot, a =>
            {
                a.Property(p => p.RecipientName).HasMaxLength(160).HasColumnName("ShippingRecipientName");
                a.Property(p => p.PhoneNumber).HasMaxLength(32).HasColumnName("ShippingPhoneNumber");
                a.Property(p => p.State).HasMaxLength(80).HasColumnName("ShippingState");
                a.Property(p => p.City).HasMaxLength(120).HasColumnName("ShippingCity");
                a.Property(p => p.Street).HasMaxLength(300).HasColumnName("ShippingStreet");
                a.Property(p => p.Landmark).HasMaxLength(200).HasColumnName("ShippingLandmark");
                a.Property(p => p.PostalCode).HasMaxLength(32).HasColumnName("ShippingPostalCode");
            });

            b.HasMany(x => x.Items)
                .WithOne()
                .HasForeignKey(i => i.OrderId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(x => x.StatusHistory)
                .WithOne()
                .HasForeignKey(h => h.OrderId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<OrderItem>(b =>
        {
            b.Property(x => x.ProductName).HasMaxLength(200).IsRequired();
            b.Property(x => x.VariantName).HasMaxLength(160).IsRequired();
            b.Property(x => x.Sku).HasMaxLength(64).IsRequired();
            b.Property(x => x.ImageUrl).HasMaxLength(500);
            b.Property(x => x.UnitPrice).HasPrecision(18, 2);
            b.Property(x => x.LineTotal).HasPrecision(18, 2);
        });

        builder.Entity<OrderStatusHistory>(b =>
        {
            b.Property(x => x.Status).HasMaxLength(40).IsRequired();
            b.Property(x => x.Note).HasMaxLength(500);
            b.Property(x => x.Actor).HasMaxLength(200);
        });

        builder.Entity<OrderDelivery>(b =>
        {
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.RecipientName).HasMaxLength(160).IsRequired();
            b.Property(x => x.PhoneNumber).HasMaxLength(32).IsRequired();
            b.Property(x => x.AddressLine).HasMaxLength(500).IsRequired();
            b.Property(x => x.State).HasMaxLength(80).IsRequired();
            b.Property(x => x.City).HasMaxLength(120).IsRequired();
            b.Property(x => x.DeliveryInstructions).HasMaxLength(500);
            b.Property(x => x.RiderName).HasMaxLength(160);
            b.Property(x => x.RiderPhone).HasMaxLength(32);
            b.Property(x => x.ReceivedBy).HasMaxLength(160);
            b.Property(x => x.LastException).HasMaxLength(500);
            b.HasIndex(x => x.OrderId).IsUnique();
            b.HasMany(x => x.History)
                .WithOne()
                .HasForeignKey(h => h.DeliveryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<DeliveryStatusHistory>(b =>
        {
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Note).HasMaxLength(500);
            b.Property(x => x.Actor).HasMaxLength(200);
        });

        builder.Entity<Payment>(b =>
        {
            b.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
            b.Property(x => x.CustomerEmail).HasMaxLength(200).IsRequired();
            b.Property(x => x.Reference).HasMaxLength(100).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.RefundedAmount).HasPrecision(18, 2);
            b.Property(x => x.Currency).HasMaxLength(8).IsRequired();
            b.Property(x => x.AuthorizationUrl).HasMaxLength(600);
            b.Property(x => x.AccessCode).HasMaxLength(120);
            b.Property(x => x.Channel).HasMaxLength(60);
            b.Property(x => x.GatewayResponse).HasMaxLength(400);
            b.Property(x => x.ProviderTransactionId).HasMaxLength(120);
            b.Property(x => x.RowVersion).IsConcurrencyToken();

            b.HasIndex(x => x.Reference).IsUnique();
            b.HasIndex(x => x.OrderId).IsUnique();

            b.HasMany(x => x.Attempts)
                .WithOne()
                .HasForeignKey(a => a.PaymentId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(x => x.Refunds)
                .WithOne()
                .HasForeignKey(r => r.PaymentId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(x => x.History)
                .WithOne()
                .HasForeignKey(h => h.PaymentId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        builder.Entity<PaymentAttempt>(b =>
        {
            b.Property(x => x.Reference).HasMaxLength(100).IsRequired();
            b.Property(x => x.Status).HasMaxLength(40).IsRequired();
            b.Property(x => x.AuthorizationUrl).HasMaxLength(600);
            b.Property(x => x.ErrorMessage).HasMaxLength(500);
            b.HasIndex(x => x.Reference).IsUnique();
        });

        builder.Entity<PaymentStatusHistory>(b =>
        {
            b.Property(x => x.Status).HasMaxLength(40).IsRequired();
            b.Property(x => x.Note).HasMaxLength(500);
            b.Property(x => x.Actor).HasMaxLength(200);
        });

        builder.Entity<PaymentWebhookEvent>(b =>
        {
            b.Property(x => x.DedupKey).HasMaxLength(128).IsRequired();
            b.Property(x => x.EventType).HasMaxLength(80).IsRequired();
            b.Property(x => x.Reference).HasMaxLength(100);
            b.Property(x => x.ProcessingError).HasMaxLength(1000);
            b.HasIndex(x => x.DedupKey).IsUnique();
            b.HasIndex(x => new { x.IsProcessed, x.CreatedAt });
        });

        builder.Entity<Refund>(b =>
        {
            b.Property(x => x.PaymentReference).HasMaxLength(100).IsRequired();
            b.Property(x => x.OrderNumber).HasMaxLength(40).IsRequired();
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.Currency).HasMaxLength(8).IsRequired();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
            b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            b.Property(x => x.ProviderRefundId).HasMaxLength(120);
            b.Property(x => x.RequestedBy).HasMaxLength(200);
            b.Property(x => x.FailureReason).HasMaxLength(500);
            b.HasIndex(x => x.OrderId);
        });

        builder.Entity<ReconciliationRecord>(b =>
        {
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(64);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            b.Property(x => x.PaymentReference).HasMaxLength(100);
            b.Property(x => x.OrderNumber).HasMaxLength(40);
            b.Property(x => x.ExpectedAmount).HasPrecision(18, 2);
            b.Property(x => x.ActualAmount).HasPrecision(18, 2);
            b.Property(x => x.Currency).HasMaxLength(8);
            b.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            b.Property(x => x.ResolvedBy).HasMaxLength(200);
            b.Property(x => x.ResolutionNote).HasMaxLength(600);
            b.HasIndex(x => new { x.Status, x.Type });
        });

        builder.Entity<AuditLog>(b =>
        {
            b.Property(x => x.Actor).HasMaxLength(200);
            b.Property(x => x.Action).HasMaxLength(100).IsRequired();
            b.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
            b.Property(x => x.EntityId).HasMaxLength(100);
            b.Property(x => x.Details).HasMaxLength(1000);
            b.Property(x => x.IpAddress).HasMaxLength(64);
            b.HasIndex(x => x.CreatedAt);
        });

        builder.Entity<Notification>(b =>
        {
            b.Property(x => x.Email).HasMaxLength(200).IsRequired();
            b.Property(x => x.Type).HasMaxLength(64).IsRequired();
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
            b.Property(x => x.Message).HasMaxLength(1000).IsRequired();
            b.Property(x => x.LastEmailError).HasMaxLength(500);
            b.HasIndex(x => new { x.UserId, x.IsRead, x.CreatedAt });
        });

        builder.Entity<ApplicationSetting>(b =>
        {
            b.Property(x => x.Key).HasMaxLength(100).IsRequired();
            b.Property(x => x.Value).HasMaxLength(1000).IsRequired();
            b.Property(x => x.Description).HasMaxLength(400);
            b.HasIndex(x => x.Key).IsUnique();
        });

        foreach (var entityType in builder.Model.GetEntityTypes())
        {
            if (typeof(DrinksEcommerce.Domain.Common.BaseEntity).IsAssignableFrom(entityType.ClrType))
            {
                builder.Entity(entityType.ClrType)
                    .Property(nameof(DrinksEcommerce.Domain.Common.BaseEntity.Id))
                    .ValueGeneratedNever();
            }
        }
    }
}

public sealed class ApplicationDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<ApplicationDbContext>
{
    public ApplicationDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<ApplicationDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=(localdb)\\mssqllocaldb;Database=DrinksEcommerceDb;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True",
            sql => sql.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName));
        return new ApplicationDbContext(optionsBuilder.Options);
    }
}
