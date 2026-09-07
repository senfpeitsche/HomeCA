using System.Globalization;
using System.Xml.Linq;
using HomeCA.Service.Localization;
using HomeCA.Service.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;

namespace HomeCA.Tests;

public sealed class LocalizationCatalogTests
{
    [Fact]
    public void English_is_the_default_and_unknown_cultures_fall_back_to_English()
    {
        Assert.Equal("en", LocaleCatalog.DefaultCulture);
        Assert.Equal("en", LocaleCatalog.NormalizeOrDefault(null));
        Assert.Equal("en", LocaleCatalog.NormalizeOrDefault("fr"));
        Assert.Equal("de", LocaleCatalog.NormalizeOrDefault("DE"));
    }

    [Fact]
    public void UI_resources_follow_the_active_request_culture()
    {
        var resources = new System.Resources.ResourceManager(
            "HomeCA.Service.Resources.UiText", typeof(UiText).Assembly);

        Assert.Equal("Sign in", resources.GetString("LoginButton", CultureInfo.GetCultureInfo("en")));
        Assert.Equal("Anmelden", resources.GetString("LoginButton", CultureInfo.GetCultureInfo("de")));
    }

    [Fact]
    public async Task Request_culture_cookie_preserves_a_selected_supported_culture()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = ".AspNetCore.Culture=c=de|uic=de";

        var result = await new CookieRequestCultureProvider().DetermineProviderCultureResult(context);

        Assert.NotNull(result);
        Assert.Equal("de", result!.Cultures.Single().Value);
        Assert.Equal("de", result.UICultures.Single().Value);
    }

    [Fact]
    public void Dates_follow_the_active_culture_without_changing_the_value()
    {
        var original = CultureInfo.CurrentCulture;
        var timestamp = new DateTime(2026, 9, 6, 14, 30, 0, DateTimeKind.Local);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en");
            var english = timestamp.ToString("d", CultureInfo.CurrentCulture);

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de");
            var german = timestamp.ToString("d", CultureInfo.CurrentCulture);

            Assert.NotEqual(english, german);
            Assert.Equal(new DateTime(2026, 9, 6, 14, 30, 0, DateTimeKind.Local), timestamp);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Every_registered_resource_locale_has_the_English_baseline_keys()
    {
        var resourceDirectory = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "HomeCA.Service", "Resources");
        resourceDirectory = Path.GetFullPath(resourceDirectory);
        var baseline = ReadKeys(Path.Combine(resourceDirectory, "UiText.resx"));

        foreach (var locale in LocaleCatalog.Locales)
        {
            var path = locale.Culture == LocaleCatalog.DefaultCulture
                ? Path.Combine(resourceDirectory, "UiText.resx")
                : Path.Combine(resourceDirectory, $"UiText.{locale.Culture}.resx");
            var missing = baseline.Except(ReadKeys(path)).OrderBy(key => key).ToArray();
            Assert.True(missing.Length == 0,
                $"Locale '{locale.Culture}' is missing UI resource keys: {string.Join(", ", missing)}");
        }
    }

    [Fact]
    public void Production_locales_provide_every_required_help_topic()
    {
        var root = FindRepositoryRoot();
        var helpRoot = Path.Combine(root, "src", "HomeCA.Service", "Help");

        foreach (var locale in LocaleCatalog.Locales.Where(locale => locale.RequiresCompleteHelp))
        foreach (var topic in LocaleCatalog.HelpTopics)
        {
            Assert.True(File.Exists(Path.Combine(helpRoot, locale.Culture, $"{topic}.md")),
                $"Locale '{locale.Culture}' is missing required help topic '{topic}'.");
        }
    }

    [Fact]
    public void Optional_locale_can_fall_back_to_English_per_help_topic()
    {
        var root = FindRepositoryRoot();
        var englishArticle = Path.Combine(root, "src", "HomeCA.Service", "Help", LocaleCatalog.DefaultCulture, "acme.md");
        var optionalLocaleArticle = Path.Combine(root, "src", "HomeCA.Service", "Help", "fr", "acme.md");

        Assert.True(File.Exists(englishArticle));
        Assert.False(File.Exists(optionalLocaleArticle));
    }

    [Fact]
    public void Help_markdown_renders_tables_and_code_but_not_unsafe_HTML()
    {
        var html = SafeMarkdown.Render("| Key | Value |\n| --- | --- |\n| API | `/api/v1` |\n\n```sh\necho safe\n```\n\n<script>alert('unsafe')</script>");

        Assert.Contains("<table>", html);
        Assert.Contains("<pre><code class=\"language-sh\">", html);
        Assert.DoesNotContain("<script>", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html);
    }

    private static HashSet<string> ReadKeys(string path) => XDocument.Load(path)
        .Root!
        .Elements("data")
        .Select(element => element.Attribute("name")!.Value)
        .ToHashSet(StringComparer.Ordinal);

    private static string FindRepositoryRoot() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
