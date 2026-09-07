## 1. Localization foundation

- [x] 1.1 Inventory every HomeCA-controlled user-visible string in Razor components, code-behind, and client-side integration points; classify technical literals that must remain stable.
- [x] 1.2 Add the supported-locale/help-topic catalog with `en` and `de`, English default/fallback semantics, and contributor-facing locale conventions.
- [x] 1.3 Configure ASP.NET Core request localization, culture providers, and culture-aware formatting with `en` as the default fallback culture.
- [x] 1.4 Add a language selector to unauthenticated and authenticated application shells, persist selection using the request-culture cookie, and render the active document language.

## 2. UI resource migration

- [x] 2.1 Create the complete English UI resource baseline, including interpolation/pluralization patterns and accessibility labels.
- [x] 2.2 Create the complete German UI resource file using the same key set.
- [x] 2.3 Replace `UiStrings` and all HomeCA-controlled direct UI literals in `Home.razor`, `SetupWizard.razor`, `CodePreview.razor`, and shared layout/components with centralized localized-resource lookups.
- [x] 2.4 Replace fixed `de-DE` date/time formatting and localized status/message construction with active-culture formatting.
- [x] 2.5 Manually verify all app surfaces in English and German: first-run setup, login, forced password change, navigation, forms, inventory tables, dialogs, validation, notifications, and accessibility labels.

## 3. Localized in-app help

- [x] 3.1 Define the safe Markdown-rendering dependency/configuration and scoped styles needed for headings, lists, tables, code blocks, and callouts.
- [x] 3.2 Extract the TLS, SSH, trust-installation, ACME, and CA-rotation English help articles from `HelpContent.razor`, preserving executable commands and technical identifiers.
- [x] 3.3 Create complete German translations of those help articles and review topic parity with English.
- [x] 3.4 Replace the inline help fragments with one locale/topic-resolving Help component, including English article fallback and a non-empty unavailable-content state.
- [x] 3.5 Verify rendered help for both cultures, including table readability, code-copy behavior, alerts, and absence of executable unsafe markup.

## 4. Quality gates and documentation

- [x] 4.1 Add tests that verify English default behavior, selection persistence, unsupported-culture fallback, and culture-aware date/time presentation.
- [x] 4.2 Add tests that compare every registered locale with the English UI-resource baseline and report missing keys.
- [x] 4.3 Add tests that verify required help topics exist for production-supported locales and that optional locales fall back to English per topic.
- [x] 4.4 Document the contributor workflow for adding a locale, translating UI resources, translating help articles, and running localization checks.
- [x] 4.5 Run the complete test suite and perform a final English/German UI regression review before release.
