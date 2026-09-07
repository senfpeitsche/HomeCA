# Inventar der UI-Lokalisierung

Dieses Inventar bildet die Grenze der HomeCA-UI-Lokalisierung. Es entstand mit
der Einführung des Kulturkatalogs `en`/`de` und wird bei jeder neuen UI-Fläche
aktualisiert.

## Lokalisieren

- Razor-Texte, Beschriftungen, Platzhalter, Hilfstexte, Dialogtexte, Snackbars,
  Validierungsmeldungen, Tabellenüberschriften, Navigationsbezeichnungen,
  Tooltips und ARIA-Labels in `src/HomeCA.Service/Components`.
- Dynamische Status- und Fehlermeldungen aus Komponentencode.
- Darstellung von Datum, Uhrzeit, Zahlen und pluralisierten Statuswerten.
- Ausführliche In-App-Hilfe: Text, Überschriften, Tabellen, Hinweise und
  Tab-Bezeichnungen.

Die umfangreichsten Migrationsbereiche sind `Pages/Home.razor`,
`Pages/SetupWizard.razor`, `Pages/HelpContent.razor`, `CodePreview.razor` und
`Layout/MainLayout.razor`.

## Stabil halten

- HTTP-Pfade, API-Feldnamen, ACME-/RFC-Namen, Zertifikatssubjekte,
  Fingerprints, Seriennummern, Algorithmen, DNS-Namen und gespeicherte Werte.
- Shell-/PowerShell-Befehle und Codebeispiele in Hilfeartikeln.
- Connector-IDs (`technitium`, `hetzner`), Konfigurationsschlüssel und Wire-Formate.

## Konvention

Kurze UI-Texte liegen in `Resources/UiText*.resx`, ausführliche Hilfe in
`Help/{locale}/{topic}.md`. Schlüssel benennen das sichtbare Konzept, nicht
einen bestimmten Bildschirm. Englisch ist die Grundlage; jede Produktionssprache
muss dieselben Ressourcenschlüssel und Hilfethemen enthalten.
