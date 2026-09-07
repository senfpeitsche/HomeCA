# UI localization inventory

This inventory is the boundary for HomeCA UI localization. It was produced while
introducing the `en`/`de` culture catalog and should be updated when a new UI
surface is added.

## Localize

- Razor text, labels, placeholders, helper text, dialog text, snackbars,
  validation messages, table headings, navigation labels, tooltips and ARIA
  labels in `src/HomeCA.Service/Components`.
- Dynamic status and error messages assembled in component code.
- Date, time, number and pluralized status presentation.
- Long-form in-app help prose, headings, tables, callouts and tab labels.

The largest migration areas are `Pages/Home.razor`, `Pages/SetupWizard.razor`,
`Pages/HelpContent.razor`, `CodePreview.razor`, and `Layout/MainLayout.razor`.

## Keep stable

- HTTP paths, API field names, ACME/RFC names, certificate subjects,
  fingerprints, serial numbers, algorithms, DNS names, and persisted values.
- Shell/PowerShell commands and code examples in help articles.
- Connector ids (`technitium`, `hetzner`), configuration keys and wire formats.

## Convention

Short UI text belongs in `Resources/UiText*.resx`; long help belongs in
`Help/{locale}/{topic}.md`. Keys name the user-visible concept rather than a
specific screen. English is the baseline and every production locale must have
the same resource keys and help topics.
