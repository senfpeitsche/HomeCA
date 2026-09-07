## Context

HomeCA is a .NET 10 Blazor Server application using MudBlazor. It currently registers a scoped `UiStrings` service whose `L(de, en)` switch supports German and English, defaults to German, and has no selector, persistence, or standard request-culture integration. It covers only a small portion of the UI: `Home.razor` and `SetupWizard.razor` contain many direct German labels, dialog messages, table headings, and feedback strings. Two date helpers are explicitly hard-coded to `de-DE`.

The embedded `HelpContent.razor` is 773 lines of German prose, MudBlazor markup, tables, alerts, and code snippets. Translating it by adding conditional Razor fragments would duplicate an already long component. Repository operations documentation is independently mirrored between `docs/` and `docs/en/`; it is explicitly out of scope.

## Goals / Non-Goals

**Goals:**

- Establish English as a predictable first-visit and fallback experience.
- Localize all HomeCA-controlled web UI content, not only navigation labels.
- Let contributors add a locale through data files and a small registry entry rather than application branching.
- Keep technical examples executable and preserve protocol/API identifiers.
- Provide a maintainable in-app help translation model and automated completeness checks.

**Non-Goals:**

- Translating API responses, OpenAPI metadata, persisted data, certificate subjects, audit records, deployment artifacts, or third-party service names.
- Reorganizing the existing repository documentation tree.
- Introducing user-account-specific language preferences; language remains browser-scoped.
- Automatically translating content or integrating a cloud translation service.

## Decisions

### Use ASP.NET Core request localization with `.resx` resources

Configure supported cultures (initially `en` and `de`) through ASP.NET Core localization and request-culture middleware. Make `en` the default and fallback. Put UI keys in a shared English resource baseline with a German satellite resource, replacing `UiStrings` language branches and direct UI literals.

This uses the platform convention, integrates `CultureInfo` formatting, supports standard tooling, and makes missing-key validation straightforward. JSON dictionaries would be easy to read but require custom lookup, fallback, formatting, and validation behavior; one Razor component per locale would couple language data to presentation logic.

### Persist selection using the request-culture cookie and reload

Expose a compact language selector on the login and authenticated shell. It writes the ASP.NET Core request-culture cookie and reloads the application so the new culture is established when the Blazor Server circuit starts. Use the active culture for `<html lang>`.

Changing a global culture during an active Blazor circuit risks stale renders and cross-circuit behavior. A cookie plus reload has predictable per-browser scope and remains usable before authentication. The app can later add an `Accept-Language` provider after the persisted selection provider, but a missing preference still resolves to English.

### Make embedded help content locale-specific Markdown

Extract the five built-in help topics into a locale/topic directory structure, with English as the canonical baseline and German as a complete translation. A single Help component resolves the active locale and topic, then falls back to the English file when an optional locale article is absent. Use a Markdown renderer configured to disable unsafe HTML and style supported output within the existing MudBlazor page.

Markdown separates prose and tables from component code while retaining code fences unchanged. Resource strings are suitable for short UI fragments but poor for large translated articles and tables. Locale-specific Razor components were rejected because they would duplicate layout and event-free presentation markup.

### Maintain a small locale and help-topic catalog

Create one authoritative catalog containing locale display metadata, resource culture, and required help-topic identifiers. The selector and verification tests consume it. Adding a locale therefore consists of registering it and adding its resource/help files; no switch statement gains a language case.

### Verify the baseline and production languages

Tests compare all registered resource keys with the English baseline and compare required help topic IDs with each production-supported locale. Development-only locales may use English help fallback, but `en` and `de` must be complete. Tests must identify missing locale/key/topic precisely.

## Risks / Trade-offs

- [Extraction exposes many currently hard-coded strings] → inventory every Razor component and use build/test review to prevent remaining user-facing literals.
- [Markdown rendering may not exactly replicate existing MudBlazor panels] → retain the tab layout in Razor and apply scoped styles; use custom rendering only where a callout needs a semantic MudBlazor equivalent.
- [Translations drift after future features] → key/topic completeness tests and contributor documentation make omissions release-visible.
- [Third-party MudBlazor strings remain English] → localize only HomeCA-controlled content in this change; assess MudBlazor provider localization separately if surfaced controls require it.
- [A cookie is browser-local] → this is intentional for an unauthenticated local-admin application and avoids changing persisted administrator data.

## Migration Plan

1. Introduce the culture catalog, request localization configuration, English default/fallback, and selector mechanism while keeping the current UI operational.
2. Build the English resource baseline by moving all HomeCA-controlled UI strings out of components; add the complete German resource file and remove `UiStrings`.
3. Replace fixed German formatting with active-culture formatting and verify login, setup, authenticated views, dialogs, and notifications under both cultures.
4. Extract and review the existing help topics into English and German Markdown files, add the safe renderer and English article fallback, and preserve code blocks verbatim.
5. Add completeness tests and contributor guidance. Release with `en` and `de` marked production-supported.

Rollback is a normal application rollback: resources and help files are packaged with the application, and the culture cookie is harmless to an older version. No stored PKI data or API contract migration is required.

## Open Questions

- Should English source content use neutral `en` or a region-specific `en-US` culture? The proposal uses neutral `en` to minimize future duplication.
- Do we want browser `Accept-Language` as a secondary first-visit preference, or an unconditional English first visit? The requirement currently chooses unconditional English.
- Is rendered Markdown’s visual fidelity sufficient for the existing deeply structured help, or should a small set of sanctioned extensions map callouts to MudBlazor alerts?
