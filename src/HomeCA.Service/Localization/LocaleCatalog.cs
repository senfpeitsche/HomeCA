using System.Globalization;

namespace HomeCA.Service.Localization;

public sealed record SupportedLocale(string Culture, string DisplayName, bool RequiresCompleteHelp);

/// <summary>Single registration point for selectable UI cultures and help topics.</summary>
public static class LocaleCatalog
{
    public const string DefaultCulture = "en";

    public static readonly IReadOnlyList<SupportedLocale> Locales =
    [
        new("en", "English", true),
        new("de", "Deutsch", true)
    ];

    public static readonly IReadOnlyList<string> HelpTopics =
    ["tls", "ssh", "trust", "acme", "ca-rotation"];

    public static IReadOnlyList<CultureInfo> Cultures => Locales
        .Select(locale => CultureInfo.GetCultureInfo(locale.Culture)).ToArray();

    public static bool IsSupported(string? culture) => Locales.Any(locale =>
        string.Equals(locale.Culture, culture, StringComparison.OrdinalIgnoreCase));

    public static string NormalizeOrDefault(string? culture) => IsSupported(culture)
        ? culture!.ToLowerInvariant()
        : DefaultCulture;
}
