namespace Foretop.App.Services;

/// <summary>
/// For screenshots (--anonymize): player names become "Player 1", "Player 2"… (your own account "You") and clan tags
/// are hidden, so published pictures show no one's identity. Off in normal use.
/// </summary>
public static class Anonymizer
{
    private static readonly Dictionary<string, int> Numbers = new(StringComparer.OrdinalIgnoreCase);

    public static bool Enabled { get; set; }

    /// <summary>Your own account name, shown as "You".</summary>
    public static string? Self { get; set; }

    public static string Name(string? real)
    {
        if (!Enabled || string.IsNullOrEmpty(real)) return real ?? "";
        if (Self is not null && string.Equals(real, Self, StringComparison.OrdinalIgnoreCase)) return "You";
        if (!Numbers.TryGetValue(real, out var n)) Numbers[real] = n = Numbers.Count + 1;
        return $"Player {n}";
    }

    public static string? Clan(string? tag) => Enabled ? null : tag;
}
