## ADDED Requirements

### Requirement: English default and fallback culture
The HomeCA web application SHALL use English (`en`) as its default culture and as the fallback for an absent, unsupported, or untranslated requested culture. The default culture SHALL be used before a visitor has chosen a language.

#### Scenario: First visit without a culture preference
- **WHEN** a visitor opens HomeCA without a stored supported culture preference
- **THEN** all localized UI content is rendered in English and the document language is `en`

#### Scenario: Unsupported culture preference
- **WHEN** a request presents an unsupported or malformed culture preference
- **THEN** HomeCA renders the application in English without failing the request

### Requirement: User-selectable and persisted culture
The web application SHALL expose the supported language choices on unauthenticated and authenticated screens. Selecting a language SHALL persist the culture in a browser-supported mechanism and SHALL apply it to the next rendered application view.

#### Scenario: Visitor chooses German before signing in
- **WHEN** a visitor selects German on the login screen
- **THEN** the login screen is rendered in German and a later visit from the same browser uses German until the preference changes

#### Scenario: Signed-in user changes language
- **WHEN** an authenticated user selects another supported language
- **THEN** the refreshed application renders its navigation, controls, dialogs, notifications, and help in that language

### Requirement: Complete resource-based UI localization
Every user-visible application string controlled by HomeCA SHALL be obtained from centralized localized resources rather than language branches or hard-coded prose in Razor components. This includes setup, login, navigation, forms, tables, dialogs, validation messages, alerts, status text, tooltips, ARIA labels, and notifications.

#### Scenario: Rendering the setup wizard in German
- **WHEN** the active culture is German
- **THEN** all HomeCA-controlled labels, instructions, controls, alerts, and validation feedback in the setup wizard are German

#### Scenario: Rendering a dialog in English
- **WHEN** the active culture is English
- **THEN** all HomeCA-controlled dialog titles, descriptions, actions, and feedback are English

### Requirement: Culture-aware presentation
The application SHALL format HomeCA-controlled dates, times, numbers, and pluralized messages according to the active culture, while preserving protocol identifiers, commands, API field names, certificate data, and stored values unchanged.

#### Scenario: Date displayed in English
- **WHEN** the active culture is English and a certificate expiry is displayed
- **THEN** its presentation uses an English culture format without changing the underlying timestamp

#### Scenario: Technical identifier in localized content
- **WHEN** HomeCA renders an API endpoint or certificate field name in a non-English locale
- **THEN** the endpoint or field name remains unchanged while surrounding explanatory text is localized

### Requirement: Contributor-friendly locale addition
The project SHALL document and enforce a locale convention in which adding a supported language requires adding localized resource and help-content files plus registration metadata, not editing application localization control flow.

#### Scenario: Adding a new locale
- **WHEN** a contributor adds a registered locale with complete resource and help files
- **THEN** the locale is available in the language selector without language-specific branches in Razor components

### Requirement: Localization completeness verification
Automated tests SHALL verify that every supported locale provides every required UI resource key and that an omitted key is detected before release.

#### Scenario: Missing localized UI key
- **WHEN** a supported locale lacks a required UI resource key
- **THEN** the automated verification fails and identifies the locale and key
