using System.Text.Json;
using Sheriff.Core;

namespace Sheriff.App;

/// <summary>Detached settings for nested dialogs; neither Cancel button can mutate the active catalog.</summary>
internal static class ConfigDraft
{
    internal static SheriffConfig Clone(SheriffConfig source)
    {
        var draft = JsonSerializer.Deserialize<SheriffConfig>(JsonSerializer.Serialize(source))!;
        draft.CategoryOverrides = new(draft.CategoryOverrides, StringComparer.OrdinalIgnoreCase);
        draft.CategoryLabels = new(draft.CategoryLabels, StringComparer.OrdinalIgnoreCase);
        draft.CategoryColors = new(draft.CategoryColors, StringComparer.OrdinalIgnoreCase);
        draft.PinnedAppIds = new(draft.PinnedAppIds, StringComparer.OrdinalIgnoreCase);
        draft.HiddenAppIds = new(draft.HiddenAppIds, StringComparer.OrdinalIgnoreCase);
        draft.AllowedAutoBlockedAppIds = new(draft.AllowedAutoBlockedAppIds, StringComparer.OrdinalIgnoreCase);
        draft.LlmApiKey = source.LlmApiKey;
        return draft;
    }
}
