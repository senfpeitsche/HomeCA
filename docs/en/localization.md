# Adding a HomeCA locale

HomeCA uses ASP.NET Core request localization. English (`en`) is the canonical
baseline and the fallback whenever a culture is absent, malformed, unsupported,
or has no optional help article. Production locales must be complete.

## Add a language

1. Add a `SupportedLocale` entry to `Localization/LocaleCatalog.cs`. Keep the
   culture neutral unless a regional distinction is required. Set
   `RequiresCompleteHelp` only for a release-supported language.
2. Create `Resources/UiText.<culture>.resx` with every key from
   `Resources/UiText.resx`. Use the same key names; do not add language branches
   to Razor components. Put a key in the English baseline first, then translate
   it in every production locale.
3. Add `Help/<culture>/<topic>.md` for each catalogued help topic. Keep commands,
   endpoints, API fields, certificate data and other technical identifiers
   unchanged. Optional languages may omit an article and use the English fallback.
4. Run `dotnet test tests/HomeCA.Tests/HomeCA.Tests.csproj -c Release`. The
   localization checks report every missing resource key by locale. Help checks
   report missing required locale/topic pairs.

## UI copy rules

Localize labels, validation copy, notices, dialogs, tooltips, ARIA labels, and
presented dates/numbers. Do not localize API routes, IDs, shell commands,
configuration keys, certificate fields, connector IDs, or persisted values.

Use `IStringLocalizer<UiText>` for short copy. Long-form help belongs in the
locale/topic Markdown corpus rather than Razor presentation code.
