using Lavari.Domain.Errors;

namespace Lavari.Domain.Time;

public readonly record struct JobTimeZone
{
    public const string DefaultIanaId = "Australia/Sydney";

    public string IanaId { get; }
    public TimeZoneInfo Info { get; }

    private JobTimeZone(string ianaId, TimeZoneInfo info)
    {
        IanaId = ianaId;
        Info = info;
    }

    public static JobTimeZone Create(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId))
        {
            throw new ValidationServiceException(
                "A time zone identifier is required.",
                ErrorCodes.InvalidTimeZone,
                "Select a valid IANA time zone such as Australia/Sydney.");
        }

        var trimmed = ianaId.Trim();
        if (!TryResolve(trimmed, out var canonicalIana, out var info))
        {
            throw new ValidationServiceException(
                $"'{trimmed}' is not a valid IANA time zone.",
                ErrorCodes.InvalidTimeZone,
                "Select a valid IANA time zone identifier such as Australia/Sydney or America/New_York.");
        }

        return new JobTimeZone(canonicalIana, info);
    }

    public static bool TryResolve(string? zoneId, out string canonicalIana, out TimeZoneInfo info)
    {
        canonicalIana = string.Empty;
        info = TimeZoneInfo.Utc;

        if (string.IsNullOrWhiteSpace(zoneId))
        {
            return false;
        }

        var trimmed = zoneId.Trim();

        if (trimmed.Equals("UTC", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase))
        {
            canonicalIana = "Etc/UTC";
            info = TimeZoneInfo.Utc;
            return true;
        }

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(trimmed, out var windowsId))
        {
            try
            {
                info = TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                canonicalIana = trimmed;
                return true;
            }
            catch (TimeZoneNotFoundException)
            {
                return false;
            }
        }

        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            try
            {
                var found = TimeZoneInfo.FindSystemTimeZoneById(trimmed);
                if (found.HasIanaId)
                {
                    info = found;
                    canonicalIana = trimmed;
                    return true;
                }
            }
            catch (TimeZoneNotFoundException)
            {
                return false;
            }
        }

        return false;
    }

    public DateTimeOffset ConvertLocalToUtc(DateTime localWallClock, bool? useDaylightOffsetForAmbiguous = null)
    {
        var unspecified = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);

        if (Info.IsInvalidTime(unspecified))
        {
            throw new ValidationServiceException(
                $"The local time {unspecified:yyyy-MM-dd HH:mm} does not exist in {IanaId} because of a daylight-saving transition.",
                ErrorCodes.AmbiguousOrInvalidLocalTime,
                "Choose a valid local time before or after the daylight-saving jump.");
        }

        if (Info.IsAmbiguousTime(unspecified))
        {
            if (useDaylightOffsetForAmbiguous is null)
            {
                throw new ValidationServiceException(
                    $"The local time {unspecified:yyyy-MM-dd HH:mm} occurs twice in {IanaId} because of a daylight-saving transition.",
                    ErrorCodes.AmbiguousOrInvalidLocalTime,
                    "Specify whether you mean the earlier (daylight-saving) or later (standard) occurrence.");
            }

            var offsets = Info.GetAmbiguousTimeOffsets(unspecified);
            var selectedOffset = useDaylightOffsetForAmbiguous.Value
                ? offsets.Max()
                : offsets.Min();
            return new DateTimeOffset(unspecified, selectedOffset).ToUniversalTime();
        }

        var offset = Info.GetUtcOffset(unspecified);
        return new DateTimeOffset(unspecified, offset).ToUniversalTime();
    }

    public DateTimeOffset ConvertUtcToZone(DateTimeOffset utcInstant)
        => TimeZoneInfo.ConvertTime(utcInstant.ToUniversalTime(), Info);
}
