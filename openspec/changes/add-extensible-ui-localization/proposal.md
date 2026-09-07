## Why

HomeCA is published on GitHub and already advertises German and English UI support, but German is the runtime default and most user-facing content is still hard-coded in Razor components. The current two-language `UiStrings` class does not provide a reliable fallback, persistence, culture-aware formatting, or a low-friction path for contributors to add languages.

The built-in help is particularly affected: its long-form German content is mixed with presentation code, so translating it duplicates markup and makes it easy for language versions to drift. HomeCA needs English as its dependable default while retaining complete German content and making future translations straightforward to contribute and validate.

## What Changes

- Make English (`en`) the default UI culture and the fallback for unsupported or incomplete locales.
- Add a persisted language selector that is available before authentication and in the authenticated application; applying a selection updates the rendered UI and document language.
- Replace the hand-written two-language UI string switch with centralized, culture-aware resource localization for every user-visible UI string, including setup, validation, dialogs, notifications, accessibility labels, and date/number formatting.
- Move the embedded help articles out of Razor code into locale-specific, rendered content so headings, prose, tables, warnings, and explanatory text can be translated independently while technical commands and identifiers remain stable.
- Ship complete English and German UI and in-app help coverage, and define a documented convention for adding another locale without changing application logic.
- Add automated coverage that detects missing UI translations and missing localized help articles before release.
- Keep public API contracts, stored PKI data, certificate contents, and generated machine-consumed files language-neutral.

## Capabilities

### New Capabilities

- `ui-localization`: Culture selection, persistence, resource lookup, English fallback, and culture-aware presentation for the HomeCA web application.
- `localized-help-content`: Locale-specific in-app help content, rendering, fallback behavior, and completeness validation.

### Modified Capabilities

- None.

## Impact

- Affected UI: `src/HomeCA.Service/Components/UiStrings.cs`, `App.razor`, `Pages/Home.razor`, `Pages/SetupWizard.razor`, `Pages/HelpContent.razor`, `CodePreview.razor`, the composition root in `Program.cs`, and any components added to support culture selection and help rendering.
- New localized resource and help-content directories will be added to the service project and packaged with the application.
- The application will use ASP.NET Core localization and request-culture infrastructure; a Markdown renderer may be introduced for the help corpus if no existing safe renderer is suitable.
- Tests will expand beyond the current service tests to verify default/fallback culture behavior and translation/help completeness.
- Repository documentation remains a separate concern: the existing German `docs/` and English `docs/en/` structure is not reorganized by this change.
