# Release-Prozess

Dieses Dokument beschreibt, wie ein neues HomeCA-Release erstellt wird und wie das Zusammenspiel mit den LXC-Installationsscripts funktioniert.

## Release erstellen

### 1. Version in der csproj hochzählen

In `src/HomeCA.Service/HomeCA.Service.csproj` die `<Version>` anpassen:

```xml
<Version>1.0.0</Version>
```

Committen:

```bash
git add src/HomeCA.Service/HomeCA.Service.csproj
git commit -m "chore: bump version to 1.0.0"
```

### 2. Tag setzen und pushen

```bash
git tag v1.0.0
git push origin main
git push origin v1.0.0
```

### 3. Was passiert automatisch

Der GitHub Actions Workflow (`.github/workflows/release.yml`) wird durch den Tag-Push ausgelöst und:

1. Checkt den Code aus
2. Installiert .NET 10
3. Extrahiert die Versionsnummer aus dem Tag (`v1.0.0` → `1.0.0`)
4. Baut `dotnet publish` mit `--runtime linux-x64` und setzt `-p:Version=1.0.0`
5. Packt das Ergebnis als `homeca-linux-x64.tar.gz` und als vollständiges `homeca-release-bundle.tar.gz`
6. Erzeugt `SHA256SUMS` sowie ein SPDX-SBOM (`homeca-linux-x64.spdx.json`)
7. Erstellt ein GitHub Release mit allen Artefakten als Assets

### 4. Ergebnis prüfen

Nach dem Workflow (ca. 2–3 Minuten) unter **Releases** im GitHub-Repository:

- Ein Release namens `v1.0.0` mit generierten Release-Notes
- Die Assets `homeca-linux-x64.tar.gz`, `homeca-release-bundle.tar.gz`, `SHA256SUMS` und `homeca-linux-x64.spdx.json`

Die URL für das Asset ist dann:
```
https://github.com/senfpeitsche/HomeCA/releases/download/v1.0.0/homeca-linux-x64.tar.gz
```

Installer und Updater laden ausschließlich `homeca-release-bundle.tar.gz` eines
konkreten Release-Tags und prüfen dessen SHA-256-Wert aus `SHA256SUMS`, bevor
sie Dateien entpacken oder den Dienst anhalten.

## Wie die Scripts das Release finden

| Script-Variable | latest | Bestimmte Version |
| --- | --- | --- |
| `HOMECA_VERSION=latest` | löst zuerst einen konkreten Release-Tag auf | — |
| `HOMECA_VERSION=v1.0.0` | — | `…/releases/download/v1.0.0/homeca-release-bundle.tar.gz` |

Der aufgelöste Tag wird anschließend für alle Downloads verwendet; produktive
Deployments beziehen keine Dateien aus dem `main`-Branch.

## Build-Optionen

Der Workflow baut aktuell **framework-dependent** (`--self-contained false`), d. h. .NET muss im Container installiert sein (wird vom Install-Script erledigt). Für einen self-contained Build stattdessen:

```yaml
# In .github/workflows/release.yml ändern:
--self-contained true
```

Das erzeugt ein größeres Artefakt (~80 MB statt ~15 MB), braucht aber kein vorinstalliertes .NET im Container. In dem Fall muss das Install-Script den .NET-Installationsschritt überspringen.

## Checkliste für ein neues Release

- [ ] Version in `HomeCA.Service.csproj` hochgezählt
- [ ] Änderungen committet und gepusht
- [ ] Tag gesetzt und gepusht (`git tag vX.Y.Z && git push origin vX.Y.Z`)
- [ ] GitHub Actions Workflow läuft durch (grün)
- [ ] Release enthält Bundle, `SHA256SUMS` und SPDX-SBOM
- [ ] Test-Update auf einem LXC: `HOMECA_VERSION=vX.Y.Z /opt/homeca/homeca-update.sh`
- [ ] `/api/v1/version` zeigt die neue Version
