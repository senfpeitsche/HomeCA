# Eine HomeCA-Sprache hinzufügen

HomeCA verwendet die Request-Lokalisierung von ASP.NET Core. Englisch (`en`) ist
die kanonische Grundlage und dient als Fallback, wenn eine Kultur fehlt, ungültig
oder nicht unterstützt ist oder kein optionaler Hilfeartikel vorhanden ist.
Produktiv unterstützte Sprachen müssen vollständig sein.

## Sprache hinzufügen

1. Füge einen `SupportedLocale`-Eintrag in `Localization/LocaleCatalog.cs` hinzu.
   Verwende eine neutrale Kultur, sofern keine regionale Unterscheidung nötig ist.
   Setze `RequiresCompleteHelp` nur für eine im Release unterstützte Sprache.
2. Erstelle `Resources/UiText.<culture>.resx` mit allen Schlüsseln aus
   `Resources/UiText.resx`. Verwende dieselben Schlüsselnamen; in Razor-Komponenten
   dürfen keine Sprachverzweigungen entstehen. Lege einen Schlüssel zuerst in der
   englischen Grundlage an und übersetze ihn dann in jede Produktionssprache.
3. Erstelle `Help/<culture>/<topic>.md` für jedes katalogisierte Hilfethema.
   Befehle, Endpunkte, API-Felder, Zertifikatsdaten und sonstige technische
   Bezeichner bleiben unverändert. Optionale Sprachen dürfen einen Artikel
   auslassen und nutzen dann den englischen Fallback.
4. Führe `dotnet test tests/HomeCA.Tests/HomeCA.Tests.csproj -c Release` aus.
   Die Lokalisierungsprüfungen melden je Sprache jeden fehlenden Ressourcenschlüssel.
   Die Hilfeprüfungen melden fehlende erforderliche Sprach-/Themenpaare.

## Regeln für UI-Texte

Lokalisieren: Beschriftungen, Validierungstexte, Hinweise, Dialoge, Tooltips,
ARIA-Labels sowie dargestellte Datums- und Zahlenwerte. Nicht lokalisieren:
API-Routen, IDs, Shell-Befehle, Konfigurationsschlüssel, Zertifikatsfelder,
Connector-IDs oder gespeicherte Werte.

Verwende für kurze Texte `IStringLocalizer<UiText>`. Ausführliche Hilfe gehört
in den Markdown-Bestand pro Sprache und Thema, nicht in Razor-Präsentationscode.
