using System.Text.Json;

namespace Lavari.Components.State;

/// <summary>
/// Value-snapshot dirty tracker. Reverting changes back to the baseline returns IsDirty = false.
/// </summary>
public sealed class LvDirtyTracker<T>
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);
    private string _baselineJson = string.Empty;

    public void SetBaseline(T snapshot) => _baselineJson = JsonSerializer.Serialize(snapshot, Opts);

    public bool IsDirty(T current) =>
        !string.Equals(_baselineJson, JsonSerializer.Serialize(current, Opts), StringComparison.Ordinal);
}
