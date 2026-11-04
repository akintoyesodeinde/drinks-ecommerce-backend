using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Domain.Enums;

namespace DrinksEcommerce.Domain.Delivery;

public class DeliveryZone : BaseEntity
{
    public string Name { get; private set; } = string.Empty;
    public string State { get; private set; } = string.Empty;
    public string CitiesCsv { get; private set; } = string.Empty;
    public decimal BaseFee { get; private set; }
    public decimal? FreeDeliveryThreshold { get; private set; }
    public int EstimatedMinDays { get; private set; } = 1;
    public int EstimatedMaxDays { get; private set; } = 2;
    public bool IsActive { get; private set; } = true;

    public ICollection<DeliveryFeeRule> FeeRules { get; private set; } = new List<DeliveryFeeRule>();

    public IReadOnlyList<string> GetCities() =>
        string.IsNullOrWhiteSpace(CitiesCsv)
            ? []
            : CitiesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private DeliveryZone() { }

    public DeliveryZone(
        string name,
        string state,
        IEnumerable<string> cities,
        decimal baseFee,
        decimal? freeDeliveryThreshold,
        int estimatedMinDays,
        int estimatedMaxDays,
        bool isActive = true)
    {
        Update(name, state, cities, baseFee, freeDeliveryThreshold, estimatedMinDays, estimatedMaxDays, isActive);
    }

    public void Update(
        string name,
        string state,
        IEnumerable<string> cities,
        decimal baseFee,
        decimal? freeDeliveryThreshold,
        int estimatedMinDays,
        int estimatedMaxDays,
        bool isActive)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("Delivery zone name is required.", "validation_error", 400);
        }
        if (string.IsNullOrWhiteSpace(state))
        {
            throw new DomainException("State is required for a delivery zone.", "validation_error", 400);
        }
        if (baseFee < 0m)
        {
            throw new DomainException("Base delivery fee cannot be negative.", "validation_error", 400);
        }
        if (estimatedMinDays < 0 || estimatedMaxDays < estimatedMinDays)
        {
            throw new DomainException("Estimated delivery days range is invalid.", "validation_error", 400);
        }

        Name = name.Trim();
        State = state.Trim();
        CitiesCsv = string.Join(",", cities.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        BaseFee = NigerianValueRules.RoundNaira(baseFee);
        FreeDeliveryThreshold = freeDeliveryThreshold.HasValue ? NigerianValueRules.RoundNaira(freeDeliveryThreshold.Value) : null;
        EstimatedMinDays = estimatedMinDays;
        EstimatedMaxDays = estimatedMaxDays;
        IsActive = isActive;
        Touch();
    }

    public bool Matches(string state, string? city)
    {
        if (!IsActive || !string.Equals(State, state?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cities = GetCities();
        if (cities.Count == 0 || cities.Contains("*", StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(city))
        {
            return false;
        }

        var normalizedCity = city.Trim();
        return cities.Any(c =>
            string.Equals(c, normalizedCity, StringComparison.OrdinalIgnoreCase) ||
            normalizedCity.Contains(c, StringComparison.OrdinalIgnoreCase));
    }

    public (decimal Fee, bool FreeApplied) CalculateFee(decimal orderSubtotal)
    {
        if (FreeDeliveryThreshold.HasValue && orderSubtotal >= FreeDeliveryThreshold.Value)
        {
            return (0m, true);
        }

        var matchingRule = FeeRules
            .OrderByDescending(r => r.MinOrderAmount)
            .FirstOrDefault(r =>
                orderSubtotal >= r.MinOrderAmount &&
                (!r.MaxOrderAmount.HasValue || orderSubtotal <= r.MaxOrderAmount.Value));

        var fee = matchingRule is not null ? matchingRule.Fee : BaseFee;
        return (NigerianValueRules.RoundNaira(fee), fee == 0m);
    }
}

public class DeliveryFeeRule : BaseEntity
{
    public Guid DeliveryZoneId { get; private set; }
    public decimal MinOrderAmount { get; private set; }
    public decimal? MaxOrderAmount { get; private set; }
    public decimal Fee { get; private set; }

    private DeliveryFeeRule() { }

    public DeliveryFeeRule(Guid deliveryZoneId, decimal minOrderAmount, decimal? maxOrderAmount, decimal fee)
    {
        DeliveryZoneId = deliveryZoneId;
        MinOrderAmount = NigerianValueRules.RoundNaira(minOrderAmount);
        MaxOrderAmount = maxOrderAmount.HasValue ? NigerianValueRules.RoundNaira(maxOrderAmount.Value) : null;
        Fee = NigerianValueRules.RoundNaira(fee);
    }
}

public class PickupLocation : BaseEntity
{
    public string Name { get; private set; } = string.Empty;
    public string Address { get; private set; } = string.Empty;
    public string State { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public string OpeningHours { get; private set; } = string.Empty;
    public string PhoneNumber { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;

    private PickupLocation() { }

    public PickupLocation(string name, string address, string state, string city, string openingHours, string phoneNumber, bool isActive = true)
    {
        Name = name.Trim();
        Address = address.Trim();
        State = state.Trim();
        City = city.Trim();
        OpeningHours = openingHours.Trim();
        PhoneNumber = NigerianValueRules.NormalizeNigerianPhoneOrThrow(phoneNumber);
        IsActive = isActive;
    }
}

public class OrderDelivery : BaseEntity
{
    public Guid OrderId { get; private set; }
    public DeliveryStatus Status { get; private set; } = DeliveryStatus.AwaitingDispatch;
    public string RecipientName { get; private set; } = string.Empty;
    public string PhoneNumber { get; private set; } = string.Empty;
    public string AddressLine { get; private set; } = string.Empty;
    public string State { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public string? DeliveryInstructions { get; private set; }
    public Guid? AssignedToUserId { get; private set; }
    public string? RiderName { get; private set; }
    public string? RiderPhone { get; private set; }
    public DateTimeOffset? ScheduledFor { get; private set; }
    public DateTimeOffset? DispatchedAt { get; private set; }
    public DateTimeOffset? DeliveredAt { get; private set; }
    public string? ReceivedBy { get; private set; }
    public int AttemptCount { get; private set; }
    public string? LastException { get; private set; }

    public ICollection<DeliveryStatusHistory> History { get; private set; } = new List<DeliveryStatusHistory>();

    private OrderDelivery() { }

    public OrderDelivery(
        Guid orderId,
        string recipientName,
        string phoneNumber,
        string addressLine,
        string state,
        string city,
        string? deliveryInstructions,
        string? actor)
    {
        OrderId = orderId;
        RecipientName = recipientName;
        PhoneNumber = phoneNumber;
        AddressLine = addressLine;
        State = state;
        City = city;
        DeliveryInstructions = deliveryInstructions;
        Status = DeliveryStatus.AwaitingDispatch;
        History.Add(new DeliveryStatusHistory(Id, Status, "Order eligible for dispatch.", actor));
    }

    public DeliveryStatusHistory AssignRider(Guid? staffUserId, string riderName, string riderPhone, DateTimeOffset? scheduledFor, string? actor)
    {
        if (Status is DeliveryStatus.Delivered or DeliveryStatus.Cancelled)
        {
            throw DomainException.Conflict($"Cannot assign rider when delivery is {Status}.", "invalid_state_transition");
        }
        if (string.IsNullOrWhiteSpace(riderName))
        {
            throw new DomainException("Rider name is required.", "validation_error", 400);
        }

        AssignedToUserId = staffUserId;
        RiderName = riderName.Trim();
        RiderPhone = NigerianValueRules.NormalizeNigerianPhoneOrThrow(riderPhone);
        ScheduledFor = scheduledFor;
        if (Status == DeliveryStatus.AwaitingDispatch || Status == DeliveryStatus.NotScheduled)
        {
            Status = DeliveryStatus.Assigned;
        }
        Touch();
        var entry = new DeliveryStatusHistory(Id, Status, $"Assigned to rider {RiderName} ({RiderPhone}).", actor);
        History.Add(entry);
        return entry;
    }

    public DeliveryStatusHistory Dispatch(string? note, string? actor)
    {
        if (Status is not (DeliveryStatus.AwaitingDispatch or DeliveryStatus.Assigned or DeliveryStatus.FailedAttempt))
        {
            throw DomainException.Conflict($"Cannot dispatch delivery from state {Status}.", "invalid_state_transition");
        }
        if (string.IsNullOrWhiteSpace(RiderName))
        {
            throw new DomainException("A delivery rider must be assigned before dispatching.", "validation_error", 400);
        }

        Status = DeliveryStatus.OutForDelivery;
        DispatchedAt ??= DateTimeOffset.UtcNow;
        AttemptCount++;
        Touch();
        var entry = new DeliveryStatusHistory(Id, Status, string.IsNullOrWhiteSpace(note) ? "Order dispatched with delivery rider." : note.Trim(), actor);
        History.Add(entry);
        return entry;
    }

    public DeliveryStatusHistory MarkDelivered(string? receivedBy, string? note, string? actor)
    {
        if (Status != DeliveryStatus.OutForDelivery)
        {
            throw DomainException.Conflict($"Cannot confirm delivery when status is {Status}. Order must be OutForDelivery.", "invalid_state_transition");
        }

        Status = DeliveryStatus.Delivered;
        DeliveredAt = DateTimeOffset.UtcNow;
        ReceivedBy = string.IsNullOrWhiteSpace(receivedBy) ? RecipientName : receivedBy.Trim();
        LastException = null;
        Touch();
        var entry = new DeliveryStatusHistory(Id, Status, string.IsNullOrWhiteSpace(note) ? $"Delivered and received by {ReceivedBy}." : note.Trim(), actor);
        History.Add(entry);
        return entry;
    }

    public DeliveryStatusHistory RecordException(string reason, string? note, string? actor)
    {
        if (Status is not (DeliveryStatus.OutForDelivery or DeliveryStatus.Assigned))
        {
            throw DomainException.Conflict($"Cannot record delivery exception when status is {Status}.", "invalid_state_transition");
        }

        Status = DeliveryStatus.FailedAttempt;
        LastException = string.IsNullOrWhiteSpace(note) ? reason : $"{reason}: {note.Trim()}";
        Touch();
        var entry = new DeliveryStatusHistory(Id, Status, $"Delivery exception ({reason}): {note ?? "No additional details"}", actor);
        History.Add(entry);
        return entry;
    }
}

public class DeliveryStatusHistory : BaseEntity
{
    public Guid DeliveryId { get; private set; }
    public DeliveryStatus Status { get; private set; }
    public string? Note { get; private set; }
    public string? Actor { get; private set; }

    private DeliveryStatusHistory() { }

    public DeliveryStatusHistory(Guid deliveryId, DeliveryStatus status, string? note, string? actor)
    {
        DeliveryId = deliveryId;
        Status = status;
        Note = note;
        Actor = actor;
    }
}
