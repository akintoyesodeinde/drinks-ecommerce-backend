using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Customers;
using DrinksEcommerce.Domain.Delivery;
using DrinksEcommerce.Domain.Enums;
using DrinksEcommerce.Domain.Inventory;
using DrinksEcommerce.Domain.Operations;
using DrinksEcommerce.Domain.Products;
using DrinksEcommerce.Persistence.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DrinksEcommerce.Persistence;

public static class DatabaseSeeder
{
    public static async Task InitializeAndSeedAsync(IServiceProvider services, bool isDevelopment, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DatabaseSeeder");

        if (db.Database.IsSqlServer())
        {
            if (db.Database.GetMigrations().Any())
            {
                await db.Database.MigrateAsync(ct);
            }
            else
            {
                await db.Database.EnsureCreatedAsync(ct);
            }
        }
        else
        {
            await db.Database.EnsureCreatedAsync(ct);
        }

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        foreach (var roleName in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new ApplicationRole(roleName));
            }
        }

        var config = scope.ServiceProvider.GetService<IConfiguration>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        // Bootstrap Admin from secure environment/Secrets Manager configuration (Production & Staging)
        var bootstrapAdminEmail = config?["BootstrapAdmin:Email"];
        var bootstrapAdminPassword = config?["BootstrapAdmin:Password"];
        if (!string.IsNullOrWhiteSpace(bootstrapAdminEmail) && !string.IsNullOrWhiteSpace(bootstrapAdminPassword))
        {
            var firstName = config?["BootstrapAdmin:FirstName"] ?? "Store";
            var lastName = config?["BootstrapAdmin:LastName"] ?? "Administrator";
            var phone = config?["BootstrapAdmin:Phone"] ?? "+2348030000000";
            await EnsureSeedUserAsync(userManager, bootstrapAdminEmail.Trim(), bootstrapAdminPassword, firstName, lastName, phone, Roles.Administrator);
            logger.LogInformation("Verified bootstrap administrator account {Email}.", bootstrapAdminEmail.Trim());
        }

        var seedDemoAccounts = isDevelopment || !string.Equals(config?["Database:SeedDemoAccounts"], "false", StringComparison.OrdinalIgnoreCase);
        if (seedDemoAccounts)
        {
            // CTO Sandbox & Presentation Demo Accounts (.ng)
            await EnsureSeedUserAsync(userManager, "admin@drinksecommerce.ng", "Admin@12345!", "Adaeze", "Okafor (SuperAdmin)", "+2348031000001", Roles.Administrator);
            await EnsureSeedUserAsync(userManager, "warehouse@drinksecommerce.ng", "Staff@12345!", "Tunde", "Bamidele (Warehouse)", "+2348031000002", Roles.WarehouseStaff);
            await EnsureSeedUserAsync(userManager, "rider@drinksecommerce.ng", "Rider@12345!", "Musa", "Ibrahim (Rider)", "+2348031000003", Roles.DeliveryStaff);
            var ngCustomer = await EnsureSeedUserAsync(userManager, "customer@drinksecommerce.ng", "Customer@12345!", "Chinedu", "Eze (Customer)", "+2348031000004", Roles.Customer);

            if (!await db.CustomerAddresses.AnyAsync(a => a.UserId == ngCustomer.Id, ct))
            {
                db.CustomerAddresses.Add(new CustomerAddress(
                    ngCustomer.Id,
                    "Home - Lekki Phase 1",
                    "Chinedu Eze",
                    "+2348031000004",
                    "Lagos",
                    "Lekki",
                    "14 Admiralty Way, Lekki Phase 1",
                    "Opposite Mega Plaza Gate",
                    "105102",
                    "Call on arrival at the estate gate.",
                    true));
            }

            // Local Integration Test Accounts (.local)
            await EnsureSeedUserAsync(userManager, "admin@drinksecommerce.local", "Admin#12345", "Adaeze", "Okafor", "+2348031000001", Roles.Administrator);
            await EnsureSeedUserAsync(userManager, "warehouse@drinksecommerce.local", "Warehouse#12345", "Tunde", "Bamidele", "+2348031000002", Roles.WarehouseStaff);
            await EnsureSeedUserAsync(userManager, "rider@drinksecommerce.local", "Rider#12345", "Musa", "Ibrahim", "+2348031000003", Roles.DeliveryStaff);
            var customer = await EnsureSeedUserAsync(userManager, "customer@drinksecommerce.local", "Customer#12345", "Chinedu", "Eze", "+2348031000004", Roles.Customer);

            if (!await db.CustomerAddresses.AnyAsync(a => a.UserId == customer.Id, ct))
            {
                db.CustomerAddresses.Add(new CustomerAddress(
                    customer.Id,
                    "Home - Lekki",
                    "Chinedu Eze",
                    "+2348031000004",
                    "Lagos",
                    "Lekki",
                    "14 Admiralty Way, Lekki Phase 1",
                    "Opposite Mega Plaza Gate",
                    "105102",
                    "Call on arrival at the estate gate.",
                    true));
            }
        }

        var seedCatalog = isDevelopment
            || string.Equals(config?["Database:SeedCatalog"], "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(config?["Database:SeedInContainer"], "true", StringComparison.OrdinalIgnoreCase);

        if (!seedCatalog)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        if (!await db.Categories.AnyAsync(ct))
        {
            var catSoft = new Category("Soft Drinks", "Sparkling carbonated sodas in PET bottles, cans, and crates.", "/images/products/coke-50cl.svg", 1);
            var catWater = new Category("Bottled & Sachet Water", "Pure table water in dispenser bottles, packs, and sachets.", "/images/products/eva-water.svg", 2);
            var catJuice = new Category("Fruit Juices", "100% fruit juices and nectar packs for families and events.", "/images/products/chivita-1l.svg", 3);
            var catEnergy = new Category("Energy Drinks", "Refreshing energy and isotonic beverages.", "/images/products/fearless-energy.svg", 4);
            var catMalt = new Category("Malt & Non-Alcoholic", "Rich malted drinks, chapman, and zero-alcohol celebratory drinks.", "/images/products/maltina-can.svg", 5);
            var catBundles = new Category("Party & Office Bundles", "Value packs and mixed cartons for Lagos & Abuja events.", "/images/products/party-bundle.svg", 6);
            var catAlcohol = new Category("Alcoholic Beverages", "Licensed adult beverages (disabled until age/license policy enabled).", "/images/products/star-radler.svg", 7);

            db.Categories.AddRange(catSoft, catWater, catJuice, catEnergy, catMalt, catBundles, catAlcohol);

            SeedProduct(db, catSoft.Id, "Coca-Cola Original Taste", "Coca-Cola",
                "Classic refreshing Coca-Cola carbonated soft drink brewed and bottled in Nigeria.",
                "/images/products/coke-50cl.svg", true, false,
                [("Serving Temperature", "Chilled (4°C)"), ("Packaging", "Recyclable PET / Can")],
                [
                    ("COKE-50CL-SGL", "Single Bottle (50cl)", PackType.Single, 1, 500, 400m, 450m, 150),
                    ("COKE-50CL-CTN12", "Carton (12 x 50cl PET)", PackType.Carton, 12, 500, 4500m, 4800m, 80),
                    ("COKE-33CL-CAN24", "Crate of Cans (24 x 33cl)", PackType.Crate, 24, 330, 9200m, 9600m, 45)
                ]);

            SeedProduct(db, catSoft.Id, "Fanta Orange", "Fanta",
                "Bright, bubbly orange flavoured soft drink loved across Nigeria.",
                "/images/products/fanta-50cl.svg", true, false,
                [("Flavour", "Vibrant Orange"), ("Packaging", "PET Bottle")],
                [
                    ("FANTA-50CL-SGL", "Single Bottle (50cl)", PackType.Single, 1, 500, 400m, null, 120),
                    ("FANTA-50CL-CTN12", "Carton (12 x 50cl PET)", PackType.Carton, 12, 500, 4500m, 4800m, 65)
                ]);

            SeedProduct(db, catSoft.Id, "Sprite Lemon-Lime", "Sprite",
                "Crisp lemon-lime soda with zero caffeine.",
                "/images/products/sprite-50cl.svg", false, false,
                [("Flavour", "Lemon-Lime"), ("Caffeine", "Caffeine-Free")],
                [
                    ("SPRITE-50CL-SGL", "Single Bottle (50cl)", PackType.Single, 1, 500, 400m, null, 110),
                    ("SPRITE-50CL-CTN12", "Carton (12 x 50cl PET)", PackType.Carton, 12, 500, 4500m, null, 60)
                ]);

            SeedProduct(db, catWater.Id, "Eva Premium Table Water", "Eva",
                "Triple-filtered Nigerian table water trusted for homes, offices, and hospitality.",
                "/images/products/eva-water.svg", true, false,
                [("Source", "Purified Table Water"), ("NAFDAC", "Registered")],
                [
                    ("EVA-75CL-SGL", "Single Bottle (75cl)", PackType.Single, 1, 750, 300m, null, 200),
                    ("EVA-75CL-CTN12", "Pack (12 x 75cl)", PackType.Carton, 12, 750, 3200m, 3600m, 95),
                    ("EVA-150CL-MPK6", "Shrink Pack (6 x 1.5L)", PackType.Multipack, 6, 1500, 2900m, null, 70)
                ]);

            SeedProduct(db, catWater.Id, "Aquafina Pure Water Sachet & Pack", "Aquafina",
                "Pure drinking water in convenient packs and sachet bags.",
                "/images/products/aquafina-water.svg", false, false,
                [("Purification", "7-Step HydRO-7")],
                [
                    ("AQUA-50CL-SACHET20", "Sachet Bag (20 x 50cl)", PackType.Sachet, 20, 500, 600m, null, 150),
                    ("AQUA-75CL-CTN12", "Carton (12 x 75cl PET)", PackType.Carton, 12, 750, 2800m, 3000m, 90)
                ]);

            SeedProduct(db, catJuice.Id, "Chivita 100% Real Fruit Juice", "Chivita",
                "No added sugar, no preservatives — pure pineapple & orange fruit goodness.",
                "/images/products/chivita-1l.svg", true, false,
                [("Fruit Content", "100% Juice"), ("Added Sugar", "None")],
                [
                    ("CHIV-1L-SGL", "1 Litre Tetra Pak", PackType.Single, 1, 1000, 1650m, 1800m, 85),
                    ("CHIV-1L-CTN10", "Carton (10 x 1L)", PackType.Carton, 10, 1000, 15800m, 16500m, 40)
                ]);

            SeedProduct(db, catJuice.Id, "Five Alive Pulpy Orange", "Five Alive",
                "Refreshing orange fruit drink with real juicy orange pulp.",
                "/images/products/five-alive.svg", false, false,
                [("Flavour", "Pulpy Orange")],
                [
                    ("5ALIVE-85CL-SGL", "Single Bottle (85cl)", PackType.Single, 1, 850, 1200m, null, 75),
                    ("5ALIVE-85CL-MPK6", "Multipack (6 x 85cl)", PackType.Multipack, 6, 850, 6900m, 7200m, 50)
                ]);

            SeedProduct(db, catEnergy.Id, "Fearless Energy Drink Classic", "Fearless",
                "Nigeria's favourite energy drink enriched with B-vitamins and taurine.",
                "/images/products/fearless-energy.svg", true, false,
                [("Vitamins", "B3, B5, B6, B12")],
                [
                    ("FEARLESS-50CL-SGL", "Single Bottle (50cl)", PackType.Single, 1, 500, 550m, null, 130),
                    ("FEARLESS-50CL-CTN12", "Carton (12 x 50cl)", PackType.Carton, 12, 500, 6200m, 6600m, 55)
                ]);

            SeedProduct(db, catMalt.Id, "Maltina Classic Non-Alcoholic Malt", "Maltina",
                "Nourishing non-alcoholic malt drink fortified with essential vitamins and calcium.",
                "/images/products/maltina-can.svg", true, false,
                [("Alcohol", "0.0% Non-Alcoholic"), ("Enrichment", "Vitamins A, B, C & Calcium")],
                [
                    ("MALTINA-33CL-SGL", "Single Can (33cl)", PackType.Single, 1, 330, 500m, null, 140),
                    ("MALTINA-33CL-MPK6", "6-Can Pack (6 x 33cl)", PackType.Multipack, 6, 330, 2900m, 3000m, 70),
                    ("MALTINA-33CL-CTN24", "Full Tray (24 x 33cl Cans)", PackType.Crate, 24, 330, 11200m, 12000m, 40)
                ]);

            SeedProduct(db, catMalt.Id, "Fayrouz Pineapple & Pear", "Fayrouz",
                "Premium sparkling non-alcoholic malt beverage with natural fruit flavour.",
                "/images/products/fayrouz-can.svg", false, false,
                [("Alcohol", "0.0%"), ("Flavour", "Pineapple & Pear")],
                [
                    ("FAYROUZ-33CL-MPK6", "Multipack (6 x 33cl Cans)", PackType.Multipack, 6, 330, 3100m, null, 60),
                    ("FAYROUZ-33CL-CTN24", "Carton (24 x 33cl Cans)", PackType.Carton, 24, 330, 11800m, 12400m, 30)
                ]);

            SeedProduct(db, catBundles.Id, "Owambe & Office Refreshment Bundle", "DrinksEcommerce",
                "Curated celebration bundle: 1 Carton Coca-Cola (12x50cl) + 1 Pack Eva Water (12x75cl) + 6 Cans Maltina.",
                "/images/products/party-bundle.svg", true, false,
                [("Total Units", "30 Drinks"), ("Ideal For", "Meetings, Birthdays & Family Hosting")],
                [
                    ("BUNDLE-OWAMBE-30", "30-Drink Celebration Bundle", PackType.Bundle, 30, 500, 9900m, 10800m, 35)
                ]);

            // Alcoholic item kept hidden while Store:AlcoholSalesEnabled = false
            SeedProduct(db, catAlcohol.Id, "Star Radler Citrus (Licensed Adult Item)", "Star",
                "2.0% ABV citrus beer blend — visible only when merchant alcohol policy is enabled.",
                "/images/products/star-radler.svg", false, true,
                [("ABV", "2.0%"), ("Age Restriction", "18+ Only")],
                [
                    ("STAR-RADLER-CTN12", "Carton (12 x 45cl)", PackType.Carton, 12, 450, 7800m, null, 25)
                ]);
        }

        if (!await db.DeliveryZones.AnyAsync(ct))
        {
            var lagosIsland = new DeliveryZone(
                "Lagos Island & Lekki Corridor",
                "Lagos",
                ["Victoria Island", "Ikoyi", "Lagos Island", "Lekki", "Ajah", "Oniru"],
                2000m,
                35000m,
                1,
                1);
            lagosIsland.FeeRules.Add(new DeliveryFeeRule(lagosIsland.Id, 20000m, 34999.99m, 1200m));

            var lagosMainland = new DeliveryZone(
                "Lagos Mainland Express",
                "Lagos",
                ["Ikeja", "Surulere", "Yaba", "Maryland", "Gbagada", "Ogba", "Magodo", "Festac", "Apapa"],
                2500m,
                40000m,
                1,
                2);
            lagosMainland.FeeRules.Add(new DeliveryFeeRule(lagosMainland.Id, 25000m, 39999.99m, 1500m));

            var abuja = new DeliveryZone(
                "Abuja Municipal (FCT)",
                "FCT - Abuja",
                ["Wuse", "Maitama", "Garki", "Asokoro", "Gwarinpa", "Jabi", "Utako", "Abuja"],
                3500m,
                50000m,
                1,
                2);

            var ph = new DeliveryZone(
                "Port Harcourt Metro",
                "Rivers",
                ["Port Harcourt", "GRA", "Trans Amadi", "Rumuola", "Peter Odili"],
                4000m,
                60000m,
                2,
                3);

            var ibadan = new DeliveryZone(
                "Ibadan City Hub",
                "Oyo",
                ["Ibadan", "Bodija", "Ring Road", "Challenge", "Dugbe"],
                3000m,
                45000m,
                1,
                2);

            db.DeliveryZones.AddRange(lagosIsland, lagosMainland, abuja, ph, ibadan);
        }

        if (!await db.PickupLocations.AnyAsync(ct))
        {
            db.PickupLocations.AddRange(
                new PickupLocation(
                    "Lekki Phase 1 Fulfilment Hub",
                    "Plot 22 Admiralty Way, Lekki Phase 1, Lagos",
                    "Lagos",
                    "Lekki",
                    "Mon–Sat 8:00 AM – 7:00 PM (WAT)",
                    "+2348031000010"),
                new PickupLocation(
                    "Ikeja Industrial Warehouse Counter",
                    "18 Oba Akran Avenue, Ikeja, Lagos",
                    "Lagos",
                    "Ikeja",
                    "Mon–Sat 8:00 AM – 6:00 PM (WAT)",
                    "+2348031000011"));
        }

        if (!await db.Categories.AnyAsync(a => a.Name == "Mixers & Syrups", ct) && await db.Categories.AnyAsync(ct))
        {
            db.Categories.Add(new Category("Mixers & Syrups", "Premium tonic water, ginger beer, bitters, and cocktail syrups.", "/images/products/fayrouz-can.svg", 6));
        }

        if (!await db.PickupLocations.AnyAsync(p => p.Name.Contains("Victoria Island"), ct))
        {
            db.PickupLocations.Add(new PickupLocation(
                "Victoria Island Pickup Hub",
                "Plot 14B Adeola Odeku Street, Victoria Island, Lagos",
                "Lagos",
                "Victoria Island",
                "Mon–Sat 8:00 AM – 7:00 PM (WAT)",
                "+2348031000012"));
        }

        if (!await db.Coupons.AnyAsync(ct))
        {
            db.Coupons.Add(new Coupon(
                "WELCOME10",
                "10% welcome discount on orders from ₦5,000 (capped at ₦3,000).",
                DiscountType.Percentage,
                10m,
                5000m,
                3000m,
                DateTimeOffset.UtcNow.AddDays(-7),
                DateTimeOffset.UtcNow.AddYears(1),
                1000,
                10,
                true));
        }

        if (!await db.Promotions.AnyAsync(ct))
        {
            db.Promotions.AddRange(
                new Promotion(
                    "Free Lagos Island Delivery on Orders Over ₦35,000",
                    "Stock up on cartons of water, juices, and soft drinks with zero delivery fee on qualifying orders.",
                    "/images/products/party-bundle.svg",
                    "/catalog",
                    DateTimeOffset.UtcNow.AddDays(-5),
                    DateTimeOffset.UtcNow.AddMonths(6)),
                new Promotion(
                    "10% Off Your First Order with Code WELCOME10",
                    "Apply coupon WELCOME10 at cart or checkout on orders of ₦5,000 and above.",
                    "/images/products/coke-50cl.svg",
                    "/catalog?featured=true",
                    DateTimeOffset.UtcNow.AddDays(-5),
                    DateTimeOffset.UtcNow.AddMonths(6)));
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Database initialization and development seed data verified.");
    }

    private static async Task<ApplicationUser> EnsureSeedUserAsync(
        UserManager<ApplicationUser> userManager,
        string email,
        string password,
        string firstName,
        string lastName,
        string phone,
        string role)
    {
        var existing = await userManager.FindByEmailAsync(email);
        if (existing is not null)
        {
            var needsUpdate = false;
            if (!existing.EmailConfirmed)
            {
                existing.EmailConfirmed = true;
                needsUpdate = true;
            }
            if (existing.LockoutEnd is not null || existing.AccessFailedCount > 0)
            {
                existing.LockoutEnd = null;
                existing.AccessFailedCount = 0;
                needsUpdate = true;
            }
            if (needsUpdate)
            {
                await userManager.UpdateAsync(existing);
            }

            if (!await userManager.CheckPasswordAsync(existing, password))
            {
                var resetToken = await userManager.GeneratePasswordResetTokenAsync(existing);
                await userManager.ResetPasswordAsync(existing, resetToken, password);
            }

            if (!await userManager.IsInRoleAsync(existing, role))
            {
                await userManager.AddToRoleAsync(existing, role);
            }
            return existing;
        }

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            PhoneNumber = phone,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, password);
        if (result.Succeeded)
        {
            await userManager.AddToRoleAsync(user, role);
        }

        return user;
    }

    private static void SeedProduct(
        ApplicationDbContext db,
        Guid categoryId,
        string name,
        string brand,
        string description,
        string imageUrl,
        bool isFeatured,
        bool isAlcoholic,
        (string Name, string Value)[] attrs,
        (string Sku, string Name, PackType PackType, int UnitsPerPack, int VolumeMl, decimal Price, decimal? CompareAt, int Stock)[] variants)
    {
        var product = new Product(name, description, brand, categoryId, isAlcoholic, isFeatured, true);
        product.Images.Add(new ProductImage(product.Id, imageUrl, name, 0));

        foreach (var (attrName, attrVal) in attrs)
        {
            product.Attributes.Add(new ProductAttribute(product.Id, attrName, attrVal));
        }

        foreach (var v in variants)
        {
            var variant = new ProductVariant(product.Id, v.Sku, v.Name, v.PackType, v.UnitsPerPack, v.VolumeMl, v.Price, v.CompareAt, true);
            product.Variants.Add(variant);
            db.InventoryItems.Add(new InventoryItem(variant.Id, v.Stock, 10));
            db.InventoryTransactions.Add(new InventoryTransaction(
                variant.Id,
                InventoryTransactionType.InitialStock,
                v.Stock,
                v.Stock,
                0,
                "Development seed stock",
                null,
                "DatabaseSeeder"));
        }

        db.Products.Add(product);
    }
}
