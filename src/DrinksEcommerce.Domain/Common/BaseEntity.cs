namespace DrinksEcommerce.Domain.Common;

public abstract class BaseEntity
{
    public Guid Id { get; protected set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public void Touch() => UpdatedAt = DateTimeOffset.UtcNow;
}

public static class Roles
{
    public const string Customer = "Customer";
    public const string Administrator = "Administrator";
    public const string WarehouseStaff = "WarehouseStaff";
    public const string DeliveryStaff = "DeliveryStaff";

    public static readonly string[] All = [Customer, Administrator, WarehouseStaff, DeliveryStaff];
    public static readonly string[] Staff = [Administrator, WarehouseStaff, DeliveryStaff];
}

public class DomainException : Exception
{
    public string Code { get; }
    public int HttpStatus { get; }

    public DomainException(string message, string code = "domain_error", int httpStatus = 400)
        : base(message)
    {
        Code = code;
        HttpStatus = httpStatus;
    }

    public static DomainException NotFound(string message) => new(message, "not_found", 404);
    public static DomainException Forbidden(string message = "You do not have permission to perform this action.") => new(message, "forbidden", 403);
    public static DomainException Conflict(string message, string code = "conflict") => new(message, code, 409);
    public static DomainException Unprocessable(string message, string code = "business_rule_violation") => new(message, code, 422);
}
