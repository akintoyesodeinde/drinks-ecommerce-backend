using DrinksEcommerce.Domain.Common;

namespace DrinksEcommerce.Domain.Operations;

public class AuditLog : BaseEntity
{
    public string? Actor { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityType { get; private set; } = string.Empty;
    public string? EntityId { get; private set; }
    public string? Details { get; private set; }
    public string? IpAddress { get; private set; }

    private AuditLog() { }

    public AuditLog(string? actor, string action, string entityType, string? entityId, string? details, string? ipAddress = null)
    {
        Actor = actor;
        Action = action;
        EntityType = entityType;
        EntityId = entityId;
        Details = details;
        IpAddress = ipAddress;
    }
}

public class Notification : BaseEntity
{
    public Guid UserId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string Type { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;
    public Guid? OrderId { get; private set; }
    public bool IsRead { get; private set; }
    public bool EmailDispatched { get; private set; }
    public int EmailAttemptCount { get; private set; }
    public string? LastEmailError { get; private set; }

    private Notification() { }

    public Notification(Guid userId, string email, string type, string title, string message, Guid? orderId = null)
    {
        UserId = userId;
        Email = email;
        Type = type;
        Title = title;
        Message = message;
        OrderId = orderId;
    }

    public void MarkRead()
    {
        IsRead = true;
        Touch();
    }

    public void MarkEmailDispatched()
    {
        EmailDispatched = true;
        EmailAttemptCount++;
        LastEmailError = null;
        Touch();
    }

    public void RecordEmailFailure(string error)
    {
        EmailAttemptCount++;
        LastEmailError = error.Length > 500 ? error[..500] : error;
        Touch();
    }
}

public class ApplicationSetting : BaseEntity
{
    public string Key { get; private set; } = string.Empty;
    public string Value { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    private ApplicationSetting() { }

    public ApplicationSetting(string key, string value, string? description = null)
    {
        Key = key.Trim();
        Value = value;
        Description = description;
    }

    public void SetValue(string value)
    {
        Value = value;
        Touch();
    }
}
