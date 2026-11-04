using System.Text.RegularExpressions;

namespace DrinksEcommerce.Domain.Common;

/// <summary>
/// Central monetary conversion and Nigerian phone number validation helpers.
/// All public prices are stored and transmitted in NGN (Naira) with 2 decimal places.
/// Paystack requires amounts in kobo (1 NGN = 100 kobo) as positive integers.
/// </summary>
public static partial class NigerianValueRules
{
    public const string DefaultCurrency = "NGN";
    public const string LagosTimeZoneId = "Africa/Lagos";

    public static decimal RoundNaira(decimal amount)
    {
        if (amount < 0m)
        {
            throw new DomainException("Monetary amount cannot be negative.", "invalid_amount", 400);
        }

        return decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Converts a valid NGN amount to Paystack kobo.
    /// Rejects negative values, fractional kobo (more than 2 decimal places), zero, and overflow.
    /// </summary>
    public static long ToKobo(decimal amountNgn)
    {
        if (amountNgn <= 0m)
        {
            throw new DomainException("Payment amount in NGN must be greater than zero.", "invalid_amount", 400);
        }

        var scaled = amountNgn * 100m;
        if (scaled != decimal.Truncate(scaled))
        {
            throw new DomainException("Monetary amount has unsupported sub-kobo precision.", "invalid_amount_precision", 400);
        }

        if (scaled > long.MaxValue)
        {
            throw new DomainException("Monetary amount exceeds maximum supported kobo value.", "amount_overflow", 400);
        }

        return decimal.ToInt64(scaled);
    }

    public static decimal FromKobo(long amountKobo)
    {
        if (amountKobo < 0)
        {
            throw new DomainException("Kobo amount cannot be negative.", "invalid_amount", 400);
        }

        return decimal.Round(amountKobo / 100m, 2, MidpointRounding.AwayFromZero);
    }

    public static bool TryNormalizeNigerianPhone(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var cleaned = Regex.Replace(input.Trim(), @"[\s\-\(\)]", string.Empty);
        if (cleaned.StartsWith("+234", StringComparison.Ordinal))
        {
            var rest = cleaned[4..];
            if (rest.StartsWith('0') && rest.Length == 11)
            {
                rest = rest[1..];
            }

            cleaned = "+234" + rest;
        }
        else if (cleaned.StartsWith("234", StringComparison.Ordinal) && cleaned.Length == 13)
        {
            cleaned = "+" + cleaned;
        }
        else if (cleaned.StartsWith('0') && cleaned.Length == 11)
        {
            cleaned = "+234" + cleaned[1..];
        }

        if (!NormalizedPhoneRegex().IsMatch(cleaned))
        {
            return false;
        }

        normalized = cleaned;
        return true;
    }

    public static string NormalizeNigerianPhoneOrThrow(string? input)
    {
        if (!TryNormalizeNigerianPhone(input, out var normalized))
        {
            throw new DomainException(
                "Enter a valid Nigerian mobile number (e.g. 08031234567 or +2348031234567).",
                "validation_error",
                400);
        }

        return normalized;
    }

    public static string Slugify(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Guid.NewGuid().ToString("N")[..8];
        }

        var lower = value.Trim().ToLowerInvariant();
        var clean = Regex.Replace(lower, @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(clean) ? Guid.NewGuid().ToString("N")[..8] : clean;
    }

    [GeneratedRegex(@"^\+234[789][01]\d{8}$", RegexOptions.Compiled)]
    private static partial Regex NormalizedPhoneRegex();
}
