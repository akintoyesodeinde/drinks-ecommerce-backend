using DrinksEcommerce.Application.Common;
using DrinksEcommerce.Application.Common.Interfaces;
using DrinksEcommerce.Application.Contracts;
using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Enums;
using DrinksEcommerce.Domain.Products;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DrinksEcommerce.Application.Products;

public sealed class CatalogService(
    IApplicationDbContext db,
    ICatalogCacheService cache,
    IOptions<StoreOptions> storeOptions)
{
    public async Task<PagedResult<ProductSummaryDto>> GetProductsAsync(
        string? search,
        Guid? categoryId,
        string? categorySlug,
        string? brand,
        decimal? minPrice,
        decimal? maxPrice,
        bool? inStockOnly,
        bool? featured,
        string? sort,
        int page = 1,
        int pageSize = 12,
        CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var allowAlcohol = storeOptions.Value.AlcoholSalesEnabled;

        var query = db.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Images)
            .Include(p => p.Variants)
                .ThenInclude(v => v.Inventory)
            .Where(p => p.IsActive && p.Category != null && p.Category.IsActive);

        if (!allowAlcohol)
        {
            query = query.Where(p => !p.IsAlcoholic);
        }

        if (categoryId.HasValue && categoryId.Value != Guid.Empty)
        {
            query = query.Where(p => p.CategoryId == categoryId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(categorySlug))
        {
            var slug = categorySlug.Trim().ToLowerInvariant();
            query = query.Where(p => p.Category!.Slug == slug);
        }

        if (!string.IsNullOrWhiteSpace(brand))
        {
            var b = brand.Trim().ToLowerInvariant();
            query = query.Where(p => p.Brand.ToLower() == b);
        }

        if (featured == true)
        {
            query = query.Where(p => p.IsFeatured);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(p =>
                p.Name.ToLower().Contains(term) ||
                p.Brand.ToLower().Contains(term) ||
                p.Description.ToLower().Contains(term) ||
                p.Variants.Any(v => v.Sku.ToLower().Contains(term) || v.Name.ToLower().Contains(term)));
        }

        var products = await query.ToListAsync(ct);

        var summaries = products
            .Select(MapProductSummary)
            .Where(s => s.VariantCount > 0)
            .Where(s => !minPrice.HasValue || s.MaxPrice >= minPrice.Value)
            .Where(s => !maxPrice.HasValue || s.MinPrice <= maxPrice.Value)
            .Where(s => inStockOnly != true || s.InStock)
            .ToList();

        summaries = (sort?.ToLowerInvariant()) switch
        {
            "name" => summaries.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "price_asc" => summaries.OrderBy(s => s.MinPrice).ThenBy(s => s.Name).ToList(),
            "price_desc" => summaries.OrderByDescending(s => s.MaxPrice).ThenBy(s => s.Name).ToList(),
            "newest" => summaries.OrderByDescending(s => products.First(p => p.Id.ToString() == s.Id).CreatedAt).ToList(),
            _ => summaries.OrderByDescending(s => s.IsFeatured).ThenByDescending(s => s.InStock).ThenBy(s => s.Name).ToList()
        };

        var totalCount = summaries.Count;
        var paged = summaries.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return PagedResult<ProductSummaryDto>.Create(paged, page, pageSize, totalCount);
    }

    public async Task<ProductDetailDto> GetProductByIdOrSlugAsync(string idOrSlug, CancellationToken ct = default)
    {
        var allowAlcohol = storeOptions.Value.AlcoholSalesEnabled;
        var isGuid = Guid.TryParse(idOrSlug, out var productId);
        var slug = idOrSlug.Trim().ToLowerInvariant();

        var product = await db.Products
            .AsNoTracking()
            .Include(p => p.Category)
            .Include(p => p.Images)
            .Include(p => p.Attributes)
            .Include(p => p.Variants)
                .ThenInclude(v => v.Inventory)
            .FirstOrDefaultAsync(p =>
                p.IsActive &&
                (isGuid ? p.Id == productId : p.Slug == slug), ct);

        if (product is null || (!allowAlcohol && product.IsAlcoholic))
        {
            throw DomainException.NotFound("Product not found.");
        }

        return MapProductDetail(product);
    }

    public async Task<IReadOnlyList<CategoryDto>> GetCategoriesAsync(CancellationToken ct = default)
    {
        return await cache.GetOrCreateAsync(
            "catalog:categories:v1",
            async token =>
            {
                var allowAlcohol = storeOptions.Value.AlcoholSalesEnabled;
                var categories = await db.Categories
                    .AsNoTracking()
                    .Include(c => c.Products)
                    .Where(c => c.IsActive)
                    .OrderBy(c => c.SortOrder)
                    .ThenBy(c => c.Name)
                    .ToListAsync(token);

                return (IReadOnlyList<CategoryDto>)categories
                    .Select(c => new CategoryDto(
                        c.Id.ToString(),
                        c.Name,
                        c.Slug,
                        c.Description,
                        c.ImageUrl,
                        c.Products.Count(p => p.IsActive && (allowAlcohol || !p.IsAlcoholic))))
                    .ToList();
            },
            TimeSpan.FromMinutes(5),
            ct) ?? [];
    }

    public async Task<CategoryDto> GetCategoryByIdOrSlugAsync(string idOrSlug, CancellationToken ct = default)
    {
        var categories = await GetCategoriesAsync(ct);
        var isGuid = Guid.TryParse(idOrSlug, out var id);
        var match = categories.FirstOrDefault(c =>
            isGuid
                ? string.Equals(c.Id, id.ToString(), StringComparison.OrdinalIgnoreCase)
                : string.Equals(c.Slug, idOrSlug.Trim(), StringComparison.OrdinalIgnoreCase));

        return match ?? throw DomainException.NotFound("Category not found.");
    }

    public async Task<IReadOnlyList<string>> GetBrandsAsync(CancellationToken ct = default)
    {
        var allowAlcohol = storeOptions.Value.AlcoholSalesEnabled;
        return await db.Products
            .AsNoTracking()
            .Where(p => p.IsActive && (allowAlcohol || !p.IsAlcoholic))
            .Select(p => p.Brand)
            .Distinct()
            .OrderBy(b => b)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PromotionDto>> GetActivePromotionsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var promos = await db.Promotions
            .AsNoTracking()
            .Where(p => p.IsActive && p.StartsAt <= now && (p.EndsAt == null || p.EndsAt >= now))
            .OrderByDescending(p => p.StartsAt)
            .ToListAsync(ct);

        return promos
            .Select(p => new PromotionDto(p.Id.ToString(), p.Title, p.Description, p.ImageUrl, p.LinkUrl, p.StartsAt, p.EndsAt))
            .ToList();
    }

    public static VariantDto MapVariant(ProductVariant v)
    {
        var available = v.Inventory?.Available ?? 0;
        var status = v.Inventory?.GetStockStatus() ?? StockStatus.OutOfStock;
        return new VariantDto(
            v.Id.ToString(),
            v.Sku,
            v.Name,
            v.PackType,
            v.UnitsPerPack,
            v.UnitVolumeMl,
            v.Price,
            v.CompareAtPrice,
            status,
            Math.Min(99, available),
            v.IsActive);
    }

    public static ProductSummaryDto MapProductSummary(Product p)
    {
        var activeVariants = p.Variants
            .Where(v => v.IsActive)
            .OrderBy(v => v.Price)
            .Select(MapVariant)
            .ToList();

        var defaultVariant = activeVariants.FirstOrDefault(v => v.AvailableQuantity > 0) ?? activeVariants.FirstOrDefault();
        var minPrice = activeVariants.Count > 0 ? activeVariants.Min(v => v.Price) : 0m;
        var maxPrice = activeVariants.Count > 0 ? activeVariants.Max(v => v.Price) : 0m;
        var inStock = activeVariants.Any(v => v.AvailableQuantity > 0);
        var primaryImage = p.Images.OrderBy(i => i.SortOrder).FirstOrDefault()?.Url;

        return new ProductSummaryDto(
            p.Id.ToString(),
            p.Slug,
            p.Name,
            p.Brand,
            p.CategoryId.ToString(),
            p.Category?.Name ?? string.Empty,
            p.Category?.Slug ?? string.Empty,
            primaryImage,
            minPrice,
            maxPrice,
            inStock,
            p.IsAlcoholic,
            p.IsFeatured,
            activeVariants.Count,
            defaultVariant);
    }

    public static ProductDetailDto MapProductDetail(Product p)
    {
        var variants = p.Variants
            .Where(v => v.IsActive)
            .OrderBy(v => v.UnitsPerPack)
            .ThenBy(v => v.Price)
            .Select(MapVariant)
            .ToList();

        var images = p.Images
            .OrderBy(i => i.SortOrder)
            .Select(i => new ProductImageDto(i.Id.ToString(), i.Url, i.AltText, i.SortOrder))
            .ToList();

        var attrs = p.Attributes
            .OrderBy(a => a.Name)
            .Select(a => new ProductAttributeDto(a.Name, a.Value))
            .ToList();

        return new ProductDetailDto(
            p.Id.ToString(),
            p.Slug,
            p.Name,
            p.Description,
            p.Brand,
            p.CategoryId.ToString(),
            p.Category?.Name ?? string.Empty,
            p.Category?.Slug ?? string.Empty,
            p.IsAlcoholic,
            p.IsFeatured,
            images,
            attrs,
            variants);
    }
}
