# Implementation checkpoint

## Change state

- Change: `add-extensible-ui-localization`
- Schema: `spec-driven`
- OpenSpec checklist: 19/19 tasks complete.
- Final verification is complete: the localized UI and help were exercised in English and German in an isolated local test instance.

## Completed localization work

- ASP.NET Core request localization, culture catalog, language selector, Markdown help renderer, and localization tests were already introduced.
- `UiStrings` was removed, its service registration was removed, and there are no remaining `UiStrings` or `L.*` call sites.
- `UiText.resx` and `UiText.de.resx` have matching resource keys. Add every new key to both files.
- `SetupWizard.razor`, shared help/layout surfaces, and many `Home.razor` strings have been migrated.
- Recent `Home.razor` migrations include:
  - certificate/CA/SSH inventories and certificate-table controls;
  - settings/TLS/setup-reset surfaces;
  - target-profile, SSH certificate, and renewal-plan forms;
  - login, validation, deletion, ACME, connector, backup, TLS, and notification runtime messages.

## Completed task 2.3 review

All HomeCA-controlled component text now uses `UiText` resources. The final component scan found only intentionally stable technical literals: protocol and format names, API routes, command snippets, IDs, algorithm names, status codes, filenames, third-party product names, and example values.

## Final validation

After each coherent migration batch run:

```powershell
dotnet build src/HomeCA.Service/HomeCA.Service.csproj -v minimal
```

This command needs the existing elevated approval because the normal sandbox cannot read the machine-wide NuGet configuration. The latest build completed successfully with **0 warnings and 0 errors**.

The final build completed with 0 warnings and 0 errors. The full test project passed 61/61 tests. Browser verification covered the first-run setup, login, forced password-change dialog, navigation/form surfaces, inventory tables, English/German language switching, all five help articles, rendered tables and callouts, localized code-copy ARIA labels, successful code copying, and the absence of rendered executable markup.

Run the test project when making subsequent localization changes:

```powershell
dotnet test tests/HomeCA.Tests/HomeCA.Tests.csproj -v minimal
```

The last full test run passed **61/61**.

## Process constraints

- Read `openspec instructions apply --change "add-extensible-ui-localization" --json` and all returned context files before resuming.
- Update `tasks.md` immediately only when a task is genuinely complete.
- Use `IStringLocalizer<HomeCA.Service.Resources.UiText>` and `Text["Key"]` / `Text["Key", argument]` in Razor and code-behind.
- Maintain exact English/German resource-key parity; the existing localization tests enforce it.
