using System.Text.Json;

namespace Lavari.Infrastructure.Integrations;

internal static class UntrustedJson
{
    public static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
            return element.EnumerateArray().Any(HasDuplicateProperties);
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        return false;
    }
}
