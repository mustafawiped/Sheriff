namespace Sheriff.Core;

/// <summary>Edits category presentation without changing the IDs used by rules and assignments.</summary>
public static class CategoryCustomization
{
    public static string GetDisplayName(SheriffConfig config, string id)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var label = FindValue(config.CategoryLabels, id);
        return IsValidName(label) ? label!.Trim() : id;
    }

    public static string? GetColor(SheriffConfig config, string id)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return NormalizeColor(FindValue(config.CategoryColors, id));
    }

    public static string AddCategory(SheriffConfig config, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        var label = ValidateName(name);
        EnsureUnique(config, label);
        if (KnownIds(config).Count() >= 30) throw new ArgumentException("En fazla 30 kategori oluşturulabilir.", nameof(name));
        config.Categories ??= [];
        config.AvailableCategories ??= [];
        config.Categories.Add(label);
        config.AvailableCategories.Add(label);
        return label;
    }

    public static void RenameCategory(SheriffConfig config, string id, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        var canonical = RequireCategory(config, id);
        var label = ValidateName(name);
        EnsureUnique(config, label, canonical);
        config.CategoryLabels = CopyMetadata(config.CategoryLabels);
        if (string.Equals(canonical, label, StringComparison.Ordinal)) config.CategoryLabels.Remove(canonical);
        else config.CategoryLabels[canonical] = label;
    }

    public static void SetColor(SheriffConfig config, string id, string? hex)
    {
        ArgumentNullException.ThrowIfNull(config);
        var canonical = RequireCategory(config, id);
        var color = NormalizeColor(hex);
        if (!string.IsNullOrWhiteSpace(hex) && color is null)
            throw new ArgumentException("Rengi #RRGGBB biçiminde girin.", nameof(hex));
        config.CategoryColors = CopyMetadata(config.CategoryColors);
        if (color is null) config.CategoryColors.Remove(canonical);
        else config.CategoryColors[canonical] = color;
    }

    public static void AssignCategory(SheriffConfig config, string appId, string id)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var canonical = RequireCategory(config, id);
        if (!(config.Categories ?? []).Contains(canonical, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Uygulama atamak için önce kategoriyi etkinleştirin.", nameof(id));
        config.CategoryOverrides = CopyMetadata(config.CategoryOverrides);
        config.CategoryOverrides[appId] = canonical;
    }

    /// <summary>Sets whether an app belongs to one exclusive category.</summary>
    public static void SetMembership(SheriffConfig config, string appId, string id, bool included, string? currentCategory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        var canonical = RequireCategory(config, id);
        if (included) { AssignCategory(config, appId, canonical); config.HiddenAppIds.Remove(appId); return; }
        if (!config.CategoryOverrides.TryGetValue(appId, out var current)) current = currentCategory;
        if (current is null) return;
        if (!string.Equals(current, canonical, StringComparison.OrdinalIgnoreCase)) return;
        config.CategoryOverrides = CopyMetadata(config.CategoryOverrides);
        config.CategoryOverrides[appId] = string.Equals(canonical, Categories.Other, StringComparison.OrdinalIgnoreCase) ? Categories.Junk : Categories.Other;
    }

    /// <summary>Accepts old or hand-edited JSON while discarding unusable presentation values.</summary>
    public static void NormalizeMetadata(SheriffConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config.CategoryLabels ?? [])
            if (!string.IsNullOrWhiteSpace(pair.Key) && IsValidName(pair.Value)) labels[pair.Key.Trim()] = pair.Value.Trim();
        config.CategoryLabels = labels;
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in config.CategoryColors ?? [])
            if (!string.IsNullOrWhiteSpace(pair.Key) && NormalizeColor(pair.Value) is { } color) colors[pair.Key.Trim()] = color;
        config.CategoryColors = colors;
    }

    private static IEnumerable<string> KnownIds(SheriffConfig config) => (config.Categories ?? [])
        .Concat(config.AvailableCategories ?? []).Concat(Categories.Defaults)
        .Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);

    private static string RequireCategory(SheriffConfig config, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return KnownIds(config).FirstOrDefault(candidate => string.Equals(candidate, id.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Kategori bulunamadı.", nameof(id));
    }

    private static void EnsureUnique(SheriffConfig config, string label, string? exceptId = null)
    {
        foreach (var id in KnownIds(config))
        {
            if (string.Equals(id, exceptId, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(id, label, StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetDisplayName(config, id), label, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Bu isimde bir kategori zaten var.", nameof(label));
        }
    }

    private static string ValidateName(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException("Kategori adı 1–40 karakter olmalı ve kontrol karakteri içermemelidir.", nameof(name));
        return name.Trim();
    }

    private static bool IsValidName(string? name) => !string.IsNullOrWhiteSpace(name)
        && name.Trim().Length <= 40 && !name.Any(char.IsControl);

    private static string? NormalizeColor(string? hex)
    {
        var value = hex?.Trim();
        return value is { Length: 7 } && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit)
            ? value.ToUpperInvariant() : null;
    }

    private static string? FindValue(Dictionary<string, string>? values, string key)
    {
        if (values is null) return null;
        if (values.TryGetValue(key, out var value)) return value;
        return values.FirstOrDefault(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static Dictionary<string, string> CopyMetadata(Dictionary<string, string>? values)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values ?? []) copy[pair.Key] = pair.Value;
        return copy;
    }
}
