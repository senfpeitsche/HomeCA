## ADDED Requirements

### Requirement: Locale-specific in-app help corpus
HomeCA SHALL store the long-form in-app help corpus as locale-specific content separated from Razor presentation code. The corpus SHALL cover TLS, SSH, trust installation, ACME setup, and CA rotation in each supported locale.

#### Scenario: English help view
- **WHEN** an English-culture user opens Help
- **THEN** the TLS, SSH, trust, ACME, and CA-rotation articles display English headings, prose, tables, warnings, and instructions

#### Scenario: German help view
- **WHEN** a German-culture user opens Help
- **THEN** the same help topics display complete German content

### Requirement: Safe structured help rendering
The in-app help renderer SHALL support the existing help structures required for readable guidance, including headings, paragraphs, ordered and unordered lists, tables, emphasis, code blocks, and informational or warning callouts. Rendered help SHALL not execute script or unsafe HTML supplied by content files.

#### Scenario: Code example in help
- **WHEN** a help article contains a shell or PowerShell example
- **THEN** the application presents it as a readable code block without translating its executable command text

#### Scenario: Unsafe markup in a help file
- **WHEN** help content contains script-capable or unsafe HTML markup
- **THEN** the rendered Help view does not execute or render the unsafe markup

### Requirement: Help fallback behavior
The application SHALL display the corresponding English help article when a selected supported locale does not provide a particular help article. It SHALL not show a blank help tab or fail the page.

#### Scenario: Missing translated help article
- **WHEN** a selected locale is missing its ACME help article
- **THEN** the Help view displays the English ACME article and remains usable

### Requirement: Help corpus completeness verification
Automated tests SHALL verify that the English baseline contains every registered help topic and that each production-supported locale contains every required topic before release.

#### Scenario: Missing production-language help topic
- **WHEN** German is registered as a production-supported locale but has no trust-installation article
- **THEN** the automated verification fails and identifies the missing locale and topic
