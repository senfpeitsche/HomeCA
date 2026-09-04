# Release Process

This document describes how to create a HomeCA release and how it works with the LXC installation scripts.

## Create a release

### 1. Increase the version in the project file

Update `<Version>` in `src/HomeCA.Service/HomeCA.Service.csproj`:

```xml
<Version>1.0.0</Version>
```

Commit it:

```bash
git add src/HomeCA.Service/HomeCA.Service.csproj
git commit -m "chore: bump version to 1.0.0"
```

### 2. Tag and push

```bash
git tag v1.0.0
git push origin main
git push origin v1.0.0
```

### 3. Automatic workflow

The GitHub Actions workflow (`.github/workflows/release.yml`) runs when the tag is pushed and:

1. Checks out the code.
2. Installs .NET 10.
3. Extracts the version from the tag (`v1.0.0` → `1.0.0`).
4. Runs `dotnet publish` with `--runtime linux-x64` and `-p:Version=1.0.0`.
5. Packages `homeca-linux-x64.tar.gz` and the complete `homeca-release-bundle.tar.gz`.
6. Generates `SHA256SUMS` and an SPDX SBOM (`homeca-linux-x64.spdx.json`).
7. Creates a GitHub Release with every artifact as an asset.

### 4. Verify the result

After the workflow (about 2–3 minutes), look under **Releases** in the GitHub repository for:

- A release named `v1.0.0` with generated release notes.
- The assets `homeca-linux-x64.tar.gz`, `homeca-release-bundle.tar.gz`, `SHA256SUMS`, and `homeca-linux-x64.spdx.json`.

The asset URL is then:

```
https://github.com/senfpeitsche/HomeCA/releases/download/v1.0.0/homeca-linux-x64.tar.gz
```

The installer and updater download only `homeca-release-bundle.tar.gz` for a specific release tag and verify its SHA-256 value from `SHA256SUMS` before extracting files or stopping the service.

## How scripts find a release

| Script variable | latest | Specific version |
| --- | --- | --- |
| `HOMECA_VERSION=latest` | resolves a specific release tag first | — |
| `HOMECA_VERSION=v1.0.0` | — | `…/releases/download/v1.0.0/homeca-release-bundle.tar.gz` |

The resolved tag is used for every download; production deployments never pull files from the `main` branch.

## Build options

The workflow currently creates a **framework-dependent** build (`--self-contained false`), so .NET must be installed in the container (the installation script does this). To create a self-contained build instead:

```yaml
# Change in .github/workflows/release.yml:
--self-contained true
```

This produces a larger artifact (about 80 MB rather than 15 MB) but does not need .NET preinstalled in the container. In that case the installation script must skip the .NET installation step.

## New-release checklist

- [ ] Version increased in `HomeCA.Service.csproj`
- [ ] Changes committed and pushed
- [ ] Tag created and pushed (`git tag vX.Y.Z && git push origin vX.Y.Z`)
- [ ] GitHub Actions workflow succeeds
- [ ] Release contains the bundle, `SHA256SUMS`, and SPDX SBOM
- [ ] Test update in an LXC: `HOMECA_VERSION=vX.Y.Z /opt/homeca/homeca-update.sh`
- [ ] `/api/v1/version` returns the new version
