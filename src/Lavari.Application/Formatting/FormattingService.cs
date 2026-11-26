using System.Globalization;
using Lavari.Application.Abstractions;
using Lavari.Contracts.Common;
using Lavari.Domain.Errors;
using Lavari.Domain.Money;
using Lavari.Domain.Time;

namespace Lavari.Application.Formatting;

public sealed class FormattingService : IFormattingService
{
    private const string DefaultLocale = "en-AU";
    private static readonly CultureInfo DefaultCulture = CultureInfo.GetCultureInfo(DefaultLocale);

    private static readonly Dictionary<string, string> CurrencySymbols = new(StringComparer.Ordinal)
    {
        ["AUD"] = "A$",
        ["USD"] = "US$",
        ["NZD"] = "NZ$",
        ["GBP"] = "£",
        ["EUR"] = "€",
        ["CAD"] = "CA$",
        ["SGD"] = "S$",
        ["CHF"] = "CHF ",
        ["HKD"] = "HK$",
        ["ZAR"] = "R",
        ["MYR"] = "RM",
        ["INR"] = "₹",
        ["CNY"] = "¥"
    };

    public string FormatMoney(MoneyCents money, string? locale = null)
    {
        var culture = ResolveCulture(locale);
        var amount = Math.Abs(money.ToHumanDecimal());
        var sign = money.Cents < 0 ? "-" : string.Empty;

        var symbol = money.Currency == "AUD" && culture.Name.Equals(DefaultLocale, StringComparison.OrdinalIgnoreCase)
            ? "$"
            : CurrencySymbols.TryGetValue(money.Currency, out var sym) ? sym : $"{money.Currency} ";
        return string.Create(culture, $"{sign}{symbol}{amount:N2}");
    }

    public MoneyCents ParseMoneyInput(string input, string currency, string? locale = null)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ValidationServiceException(
                "A monetary amount is required.",
                ErrorCodes.ValidationFailed,
                "Enter a valid numeric amount with at most two decimal places.");
        }

        var normalizedCurrency = MoneyCents.NormalizeAndValidateCurrency(currency);
        var culture = ResolveCulture(locale);
        var cleaned = input.Trim()
            .Replace("A$", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("US$", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("NZ$", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(normalizedCurrency, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (!decimal.TryParse(cleaned, NumberStyles.Currency | NumberStyles.AllowLeadingSign, culture, out var parsed))
        {
            throw new ValidationServiceException(
                $"'{input}' is not a valid monetary amount.",
                ErrorCodes.ValidationFailed,
                "Enter a valid number such as 1250.50.");
        }

        return MoneyCents.FromExactDecimalInput(parsed, normalizedCurrency);
    }

    public FormattedDateTimeDto FormatDateTime(
        DateTimeOffset utcInstant,
        string jobIanaTimeZone,
        string? browserIanaTimeZone = null,
        string? locale = null)
    {
        var culture = ResolveCulture(locale);
        var utc = utcInstant.ToUniversalTime();
        var jobZone = JobTimeZone.Create(jobIanaTimeZone);
        var jobLocal = jobZone.ConvertUtcToZone(utc);
        var jobOffsetLabel = FormatOffset(jobLocal.Offset);
        var jobZoneLabel = $"{jobZone.IanaId} ({jobOffsetLabel})";
        var jobFormatted = $"{jobLocal.ToString("dd MMM yyyy, h:mm tt", culture)} {jobZoneLabel}";

        if (string.IsNullOrWhiteSpace(browserIanaTimeZone))
        {
            return new FormattedDateTimeDto
            {
                Utc = utc,
                JobLocalFormatted = jobFormatted,
                JobZoneId = jobZone.IanaId,
                JobZoneLabel = jobZoneLabel,
                BrowserLocalFormatted = null,
                BrowserZoneId = null,
                BrowserZoneLabel = "Local time unavailable",
                ZonesDiffer = false,
                BrowserZoneAvailable = false
            };
        }

        if (!JobTimeZone.TryResolve(browserIanaTimeZone, out var browserIana, out var browserInfo))
        {
            return new FormattedDateTimeDto
            {
                Utc = utc,
                JobLocalFormatted = jobFormatted,
                JobZoneId = jobZone.IanaId,
                JobZoneLabel = jobZoneLabel,
                BrowserLocalFormatted = null,
                BrowserZoneId = null,
                BrowserZoneLabel = "Local time unavailable",
                ZonesDiffer = false,
                BrowserZoneAvailable = false
            };
        }

        // Keep zones distinct even if their current UTC offsets happen to match (e.g., Sydney vs Melbourne in summer, or UTC vs Europe/London in winter).
        var zonesDiffer = !string.Equals(jobZone.IanaId, browserIana, StringComparison.OrdinalIgnoreCase);
        var browserLocal = TimeZoneInfo.ConvertTime(utc, browserInfo);
        var browserOffsetLabel = FormatOffset(browserLocal.Offset);
        var browserZoneLabel = $"{browserIana} ({browserOffsetLabel})";
        var browserFormatted = $"{browserLocal.ToString("dd MMM yyyy, h:mm tt", culture)} {browserZoneLabel}";

        return new FormattedDateTimeDto
        {
            Utc = utc,
            JobLocalFormatted = jobFormatted,
            JobZoneId = jobZone.IanaId,
            JobZoneLabel = jobZoneLabel,
            BrowserLocalFormatted = browserFormatted,
            BrowserZoneId = browserIana,
            BrowserZoneLabel = browserZoneLabel,
            ZonesDiffer = zonesDiffer,
            BrowserZoneAvailable = true
        };
    }

    public string FormatCalendarDate(DateOnly date, string? locale = null)
    {
        var culture = ResolveCulture(locale);
        return date.ToString("dd MMM yyyy", culture);
    }

    public string FormatDuration(long totalSeconds)
    {
        var negative = totalSeconds < 0;
        var abs = Math.Abs(totalSeconds);
        var hours = abs / 3600;
        var minutes = (abs % 3600) / 60;
        var seconds = abs % 60;
        var prefix = negative ? "-" : string.Empty;
        return $"{prefix}{hours:D2}:{minutes:D2}:{seconds:D2}";
    }

    private static CultureInfo ResolveCulture(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return DefaultCulture;
        }

        var trimmed = locale.Trim();
        if (trimmed.Equals(DefaultLocale, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultCulture;
        }

        try
        {
            return CultureInfo.GetCultureInfo(trimmed);
        }
        catch (CultureNotFoundException)
        {
            return DefaultCulture;
        }
    }

    private static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return $"UTC{sign}{abs.Hours:D2}:{abs.Minutes:D2}";
    }
}
