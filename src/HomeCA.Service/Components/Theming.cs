namespace HomeCA.Service.Components;

/// <summary>Requested appearance; System resolves against the browser.</summary>
public enum ThemePreference
{
    System,
    Light,
    Dark
}

/// <summary>Cascaded by MainLayout so the appearance menu can read and set the preference.</summary>
public sealed class ThemeContext
{
    public ThemePreference Preference { get; internal set; } = ThemePreference.System;

    /// <summary>The value actually in effect once System has been resolved.</summary>
    public bool IsDark { get; internal set; }

    public Func<ThemePreference, Task> SetPreferenceAsync { get; internal set; } = _ => Task.CompletedTask;

    public static ThemePreference Parse(string? stored) => stored switch
    {
        "light" => ThemePreference.Light,
        "dark" => ThemePreference.Dark,
        _ => ThemePreference.System
    };

    public static string Serialize(ThemePreference preference) => preference switch
    {
        ThemePreference.Light => "light",
        ThemePreference.Dark => "dark",
        _ => "system"
    };
}
