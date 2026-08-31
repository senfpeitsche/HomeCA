# Contributing to HomeCA

Thanks for your interest in contributing. HomeCA aims to be a simple, self-hosted PKI for homelabs. Contributions that keep it simple and focused are welcome.

## Development Setup

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- `openssh-client` (for SSH certificate features)
- Any C# IDE: Rider, Visual Studio, VS Code with C# Dev Kit

### Build

```bash
dotnet build
```

### Run

```bash
dotnet run --project src/HomeCA.Service
```

The service starts at `http://localhost:5152` with the Blazor UI. In development mode (`ASPNETCORE_ENVIRONMENT=Development`), use `admin` / `foobar` to log in. No separate frontend build step is needed.

### Test

```bash
dotnet test
```

Tests run against temporary directories and clean up after themselves. No external services required.

## Project Structure

```
HomeCA.slnx                     Solution file
src/HomeCA.Service/              Main service project
  Program.cs                     Entry point, DI, all API endpoints
  Pki/                           CA management, certificate issuance, CRL
  Security/                      Auth, sessions, password hashing, rate limiting
  Acme/                          Internal + external ACME
  Connectors/                    DNS provider integrations
  Automation/                    Renewal plans and background service
  Components/                    Blazor UI
  Infrastructure/                Storage, backup, configuration
tests/HomeCA.Tests/              xUnit test project
profiles/profiles.json           Target system profile definitions (seed data)
deploy/                          systemd service + install script
docs/                            Operational documentation
```

## Conventions

- **No external database.** All state is file-based JSON. Keep it that way.
- **Minimal dependencies.** Only add NuGet packages when there's no reasonable built-in alternative.
- **German UI, English code.** Code, comments, API responses, and logs are in English. The UI defaults to German with English available via `UiStrings`. When adding UI text, add both languages.
- **Records for DTOs.** Use C# `record` types for all request/response models.
- **Singletons for services.** All domain services are registered as singletons with `SemaphoreSlim` for thread safety.
- **File-based persistence.** Each registry owns its own JSON file under `state/`. Use atomic write (write to .tmp, then move).

## Adding a Target Profile

1. Add the profile to `profiles/profiles.json` with an appropriate `id`, `keyAlgorithm`, `exportFormats`, and installation `documentation`.
2. Verify the profile loads correctly by running the app or tests.

## Adding a DNS Connector

1. Implement `IDnsConnector` in the `Connectors/` folder.
2. Register it in `Program.cs` as `AddSingleton<IDnsConnector, YourConnector>()`.
3. The `ConnectorCatalog` picks it up automatically.

## Adding UI Strings (Localization)

1. Add properties to `Components/UiStrings.cs` using the `L(de, en)` helper.
2. Reference them in Razor files via `@L.YourProperty`.
3. The login, navigation, and overview sections are already migrated as examples.

## Pull Requests

- Keep PRs focused. One feature or fix per PR.
- Include tests for new service-level logic.
- Make sure `dotnet build` and `dotnet test` pass before submitting.
- The maintainer will review and merge.

## Reporting Issues

Open an issue with:
- What you expected to happen
- What actually happened
- Steps to reproduce
- HomeCA version / .NET version / OS
