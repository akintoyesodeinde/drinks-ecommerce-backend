using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Enums;
using DrinksEcommerce.Domain.Inventory;

namespace DrinksEcommerce.Domain.Products;

public class Category : BaseEntity
{
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string? ImageUrl { get; private set; }
    public bool IsActive { get; private set; } = true;
    public int SortOrder { get; private set; }
    public ICollection<Product> Products { get; private set; } = new List<Product>();

    private Category() { }

    public Category(string name, string? description, string? imageUrl = null, int sortOrder = 0, bool isActive = true)
    {
        Update(name, description, imageUrl, sortOrder, isActive);
    }

    public void Update(string name, string? description, string? imageUrl, int sortOrder, bool isActive)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Category name is required.", "validation_error", 400);
        }

        Name = name.Trim();
        Slug = NigerianValueRules.Slugify(Name);
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (!string.IsNullOrWhiteSpace(imageUrl))
        {
            ImageUrl = imageUrl.Trim();
        }
        SortOrder = sortOrder;
        IsActive = isActive;
        Touch();
    }
}

public class Product : BaseEntity
{
    public string Name { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string Brand { get; private set; } = string.Empty;
    public Guid CategoryId { get; private set; }
    public Category? Category { get; private set; }
    public bool IsAlcoholic { get; private set; }
    public bool IsFeatured { get; private set; }
    public bool IsActive { get; private set; } = true;

    public ICollection<ProductVariant> Variants { get; private set; } = new List<ProductVariant>();
    public ICollection<ProductImage> Images { get; private set; } = new List<ProductImage>();
    public ICollection<ProductAttribute> Attributes { get; private set; } = new List<ProductAttribute>();

    private Product() { }

    public Product(
        string name,
        string description,
        string brand,
        Guid categoryId,
        bool isAlcoholic = false,
        bool isFeatured = false,
        bool isActive = true)
    {
        UpdateDetails(name, description, brand, categoryId, isAlcoholic, isFeatured, isActive);
    }

    public void UpdateDetails(
        string name,
        string description,
        string brand,
        Guid categoryId,
        bool isAlcoholic,
        bool isFeatured,
        bool isActive)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Product name is required.", "validation_error", 400);
        }
        if (string.IsNullOrWhiteSpace(brand))
        {
            throw new DomainException("Product brand is required.", "validation_error", 400);
        }
        if (categoryId == Guid.Empty)
        {
            throw new DomainException("Category is required.", "validation_error", 400);
        }

        Name = name.Trim();
        Slug = NigerianValueRules.Slugify(Name);
        Description = description?.Trim() ?? string.Empty;
        Brand = brand.Trim();
        CategoryId = categoryId;
        IsAlcoholic = isAlcoholic;
        IsFeatured = isFeatured;
        IsActive = isActive;
        Touch();
    }

    public void Deactivate()
    {
        IsActive = false;
        Touch();
    }

    public void SetSlug(string slug)
    {
        Slug = NigerianValueRules.Slugify(slug);
    }
}

public class ProductVariant : BaseEntity
{
    public Guid ProductId { get; private set; }
    public Product? Product { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public PackType PackType { get; private set; } = PackType.Single;
    public int UnitsPerPack { get; private set; } = 1;
    public int? UnitVolumeMl { get; private set; }
    public decimal Price { get; private set; }
    public decimal? CompareAtPrice { get; private set; }
    public bool IsActive { get; private set; } = true;
    public InventoryItem? Inventory { get; private set; }

    private ProductVariant() { }

    public ProductVariant(
        Guid productId,
        string sku,
        string name,
        PackType packType,
        int unitsPerPack,
        int? unitVolumeMl,
        decimal price,
        decimal? compareAtPrice = null,
        bool isActive = true)
    {
        ProductId = productId;
        Update(sku, name, packType, unitsPerPack, unitVolumeMl, price, compareAtPrice, isActive);
    }

    public void Update(
        string sku,
        string name,
        PackType packType,
        int unitsPerPack,
        int? unitVolumeMl,
        decimal price,
        decimal? compareAtPrice,
        bool isActive)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            throw new DomainException("Variant SKU is required.", "validation_error", 400);
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Variant pack name is required.", "validation_error", 400);
        }
        if (unitsPerPack < 1 || unitsPerPack > 500)
        {
            throw new DomainException("Units per pack must be between 1 and 500.", "validation_error", 400);
        }
        if (packType is PackType.Multipack or PackType.Carton or PackType.Crate && unitsPerPack < 2)
        {
            throw new DomainException($"{packType} variants must contain at least 2 units per pack.", "validation_error", 400);
        }
        if (unitVolumeMl is <= 0 or > 50000)
        {
            throw new DomainException("Unit volume in ml must be between 1 and 50,000.", "validation_error", 400);
        }
        if (price <= 0m)
        {
            throw new DomainException("Variant price must be greater than zero.", "validation_error", 400);
        }
        if (compareAtPrice.HasValue && compareAtPrice.Value <= price)
        {
            compareAtPrice = null;
        }

        Sku = sku.Trim().ToUpperInvariant();
        Name = name.Trim();
        PackType = packType;
        UnitsPerPack = unitsPerPack;
        UnitVolumeMl = unitVolumeMl;
        Price = NigerianValueRules.RoundNaira(price);
        CompareAtPrice = compareAtPrice.HasValue ? NigerianValueRules.RoundNaira(compareAtPrice.Value) : null;
        IsActive = isActive;
        Touch();
    }
}

public class ProductImage : BaseEntity
{
    public Guid ProductId { get; private set; }
    public string Url { get; private set; } = string.Empty;
    public string? AltText { get; private set; }
    public int SortOrder { get; private set; }

    private ProductImage() { }

    public ProductImage(Guid productId, string url, string? altText = null, int sortOrder = 0)
    {
        ProductId = productId;
        Url = url.Trim();
        AltText = string.IsNullOrWhiteSpace(altText) ? null : altText.Trim();
        SortOrder = sortOrder;
    }
}

public class ProductAttribute : BaseEntity
{
    public Guid ProductId { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;

    private ProductAttribute() { }

    public ProductAttribute(Guid productId, string name, string value)
    {
        ProductId = productId;
        Name = name.Trim();
        Value = value.Trim();
    }
}
