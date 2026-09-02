namespace HomeCA.Service.Components;

/// <summary>
/// Centralized UI strings for localization. Replace this class or extend it with a resource-based
/// implementation to add additional languages. The default language is German (de).
/// </summary>
public sealed class UiStrings
{
    public string Language { get; private set; } = "de";

    public void SetLanguage(string language) => Language = language switch
    {
        "en" => "en",
        _ => "de"
    };

    // ── Login ────────────────────────────────────────────────────────────────
    public string LoginSignalText => L("LOKALE ZERTIFIKATSSTELLE", "LOCAL CERTIFICATE AUTHORITY");
    public string LoginSubtitle => L("Melden Sie sich an, um Ihre lokale PKI zu verwalten.", "Sign in to manage your local PKI.");
    public string LoginUsername => L("Benutzername", "Username");
    public string LoginPassword => L("Passwort", "Password");
    public string LoginButton => L("Anmelden", "Sign in");
    public string LoginBusy => L("Anmeldung läuft …", "Signing in …");
    public string LogoutButton => L("Abmelden", "Sign out");
    public string RefreshAriaLabel => L("Daten aktualisieren", "Refresh data");

    // ── Password Change ──────────────────────────────────────────────────────
    public string ChangePasswordTitle => L("Passwort ändern", "Change Password");
    public string ChangePasswordDescription => L("Das Standardpasswort muss vor der ersten Nutzung geändert werden.", "The default password must be changed before first use.");
    public string DefaultCredentialsHint => L("Erstanmeldung: Benutzername admin, Passwort admin", "First login: username admin, password admin");
    public string CurrentPassword => L("Aktuelles Passwort", "Current Password");
    public string NewPassword => L("Neues Passwort", "New Password");
    public string ConfirmNewPassword => L("Neues Passwort bestätigen", "Confirm New Password");
    public string ChangePasswordButton => L("Passwort ändern", "Change Password");
    public string ChangePasswordBusy => L("Passwort wird geändert …", "Changing password …");
    public string ChangePasswordSuccess => L("Passwort erfolgreich geändert. Bitte melden Sie sich mit dem neuen Passwort an.", "Password changed successfully. Please sign in with your new password.");
    public string ChangePasswordError => L("Passwortänderung fehlgeschlagen. Prüfen Sie das aktuelle Passwort.", "Password change failed. Check the current password.");
    public string ChangePasswordMismatch => L("Die Passwörter stimmen nicht überein.", "Passwords do not match.");
    public string ChangePasswordTooShort => L("Das neue Passwort muss mindestens 12 Zeichen lang sein.", "The new password must be at least 12 characters.");

    // ── Trust Installation ───────────────────────────────────────────────────
    public string TrustInstallTitle => L("Zertifikatskette auf Geräten installieren", "Install Certificate Chain on Devices");
    public string TrustInstallDescription => L("Kopieren Sie das passende Skript und führen Sie es auf dem Zielgerät aus. Es installiert die HomeCA-Root-CA.", "Copy the appropriate script and run it on the target device. It installs the HomeCA root CA.");
    public string TrustInstallCopied => L("In die Zwischenablage kopiert.", "Copied to clipboard.");

    // ── Navigation ───────────────────────────────────────────────────────────
    public string NavOverview => L("Übersicht", "Overview");
    public string NavCertificates => L("Zertifikate", "Certificates");
    public string NavAuthorities => L("Zertifizierungsstellen", "Certificate Authorities");
    public string NavAutomation => L("Automatik", "Automation");
    public string NavSettings => L("Einstellungen", "Settings");
    public string NavProfiles => L("Profile", "Profiles");
    public string NavOperations => L("Betrieb", "Operations");
    public string NavHelp => L("Hilfe", "Help");

    // ── ACME ─────────────────────────────────────────────────────────────────
    public string NavAcme => L("ACME", "ACME");
    public string AcmeKicker => L("Automatische Zertifikatsverwaltung", "Automatic Certificate Management");
    public string AcmeTitle => L("ACME-Verwaltung", "ACME Management");
    public string AcmeRfc8555Accounts => L("RFC 8555-Konten (OPNsense, acme.sh, Certbot)", "RFC 8555 Accounts (OPNsense, acme.sh, Certbot)");
    public string AcmeRfc8555Orders => L("RFC 8555-Aufträge", "RFC 8555 Orders");
    public string AcmeSimplifiedAccounts => L("Vereinfachte API-Konten (curl/Skripte)", "Simplified API Accounts (curl/scripts)");
    public string AcmeSimplifiedOrders => L("Vereinfachte API-Aufträge", "Simplified API Orders");
    public string AcmeNoAccounts => L("Noch keine ACME-Konten registriert.", "No ACME accounts registered yet.");
    public string AcmeNoOrders => L("Noch keine ACME-Aufträge vorhanden.", "No ACME orders yet.");
    public string AcmeColumnContact => L("Kontakt", "Contact");
    public string AcmeColumnThumbprint => L("Schlüssel-Thumbprint", "Key Thumbprint");
    public string AcmeColumnCreated => L("Erstellt", "Created");
    public string AcmeColumnIdentifiers => L("Identitäten", "Identifiers");
    public string AcmeColumnStatus => L("Status", "Status");
    public string AcmeColumnCertificate => L("Zertifikat", "Certificate");
    public string AcmeExternalIssuers => L("Externe ACME-Aussteller", "External ACME Issuers");
    public string AcmeExternalCertificates => L("Externe ACME-Zertifikate", "External ACME Certificates");
    public string AcmeNoExternalCerts => L("Noch keine externen ACME-Zertifikate vorhanden.", "No external ACME certificates yet.");

    // ── Help ─────────────────────────────────────────────────────────────────
    public string HelpKicker => L("Anleitungen und Referenz", "Guides and Reference");
    public string HelpTitle => L("Hilfe", "Help");
    public string HelpDescription => L("Schritt-für-Schritt-Anleitungen für TLS- und SSH-Zertifikate, Vertrauensstellung und ACME-Einrichtung.", "Step-by-step guides for TLS and SSH certificates, trust installation and ACME setup.");
    public string HelpTabTls => L("TLS-Zertifikate", "TLS Certificates");
    public string HelpTabSsh => L("SSH-Zertifikate", "SSH Certificates");
    public string HelpTabTrust => L("Vertrauensstellung", "Trust Installation");
    public string HelpTabAcme => L("ACME-Einrichtung", "ACME Setup");
    public string HelpTabCaRotation => L("CA-Rotation", "CA Rotation");

    // ── Overview ─────────────────────────────────────────────────────────────
    public string OverviewKicker => L("Zustand der Vertrauensbasis", "Trust Foundation Status");
    public string OverviewTitle => L("Übersicht", "Overview");
    public string MetricCertificates => L("Zertifikate", "Certificates");
    public string MetricCAs => L("CAs", "CAs");
    public string MetricRenewals => L("Erneuerungen", "Renewals");
    public string ExpiryWarning(int count) => L($"{count} Zertifikat(e) laufen bald ab.", $"{count} certificate(s) expiring soon.");
    public string ExpiryDays(int days) => L($"{days} Tage", $"{days} days");
    public string TrustAnchorTitle => L("Vertrauensanker verteilen", "Distribute Trust Anchor");
    public string TrustAnchorDescription => L("Damit Geräte und Browser den Zertifikaten dieser PKI vertrauen, muss das Root-CA-Zertifikat im Vertrauensspeicher installiert werden. Die folgenden Endpunkte sind ohne Authentifizierung erreichbar.", "To trust certificates from this PKI, devices and browsers must install the Root CA certificate in their trust store. The following endpoints are available without authentication.");
    public string TrustAnchorPem => L("Root-CA als PEM", "Root CA as PEM");
    public string TrustAnchorDer => L("Root-CA als Windows CER", "Root CA as Windows CER");
    public string LatestCertificates => L("Neueste Zertifikate", "Latest Certificates");

    // ── Certificates ─────────────────────────────────────────────────────────
    public string CertificatesKicker => L("X.509 und SSH", "X.509 and SSH");
    public string CertificatesTitle => L("Zertifikate ausstellen", "Issue Certificates");
    public string TabTls => L("TLS- und mTLS-Zertifikat", "TLS and mTLS Certificate");
    public string TabSsh => L("SSH-Zertifikat", "SSH Certificate");
    public string TabAcme => L("Interne ACME", "Internal ACME");
    public string CertificateInventory => L("Zertifikatsinventar", "Certificate Inventory");
    public string NoCertificatesYet => L("Noch keine Zertifikate ausgestellt.", "No certificates issued yet.");
    public string ColumnSubject => L("Subject", "Subject");
    public string ColumnAlgorithm => L("Algorithmus", "Algorithm");
    public string ColumnExpiresAt => L("Läuft ab", "Expires");
    public string ColumnDownload => L("Download", "Download");

    // ── Authorities ──────────────────────────────────────────────────────────
    public string AuthoritiesKicker => L("Vertrauenshierarchie und Sperrlisten", "Trust Hierarchy and Revocation Lists");
    public string AuthoritiesTitle => L("Zertifizierungsstellen", "Certificate Authorities");
    public string CreateCa => L("CA erstellen", "Create CA");
    public string ManageCa => L("CA verwalten", "Manage CA");
    public string SaveChanges => L("Änderungen speichern", "Save Changes");
    public string CancelEdit => L("Bearbeiten abbrechen", "Cancel Edit");
    public string Edit => L("Bearbeiten", "Edit");
    public string Delete => L("Löschen", "Delete");
    public string ConfirmDelete => L("Löschen bestätigen", "Confirm Delete");
    public string Revoke => L("Sperren", "Revoke");
    public string CaInventory => L("CA-Inventar", "CA Inventory");
    public string NoRootCaYet => L("Legen Sie zuerst eine Root-CA an.", "Create a Root CA first.");
    public string NewIntermediate => L("Neue Intermediate-CA", "New Intermediate CA");

    // ── PFX Dialog ───────────────────────────────────────────────────────────
    public string PfxDialogTitle => L("PFX-Kennwort festlegen", "Set PFX Password");
    public string PfxDialogDescription => L("Das PFX-Archiv wird mit diesem Kennwort verschlüsselt. Verwenden Sie ein starkes Kennwort und geben Sie es nur an das Zielsystem weiter.", "The PFX archive will be encrypted with this password. Use a strong password and only share it with the target system.");
    public string PfxPasswordLabel => L("Kennwort", "Password");
    public string PfxConfirmLabel => L("Kennwort bestätigen", "Confirm Password");
    public string Cancel => L("Abbrechen", "Cancel");
    public string Download => L("Herunterladen", "Download");

    // ── Operations ───────────────────────────────────────────────────────────
    public string OperationsKicker => L("Sperrung und Wiederherstellbarkeit", "Revocation and Recovery");
    public string OperationsTitle => L("Betrieb", "Operations");
    public string GenerateCrl => L("Sperrliste erzeugen", "Generate CRL");
    public string BackupTitle => L("Verschlüsseltes Backup", "Encrypted Backup");
    public string BackupDescription => L("Erstellt ein verschlüsseltes Archiv der HomeCA-Daten.", "Creates an encrypted archive of the HomeCA data.");
    public string CreateBackup => L("Backup erstellen", "Create Backup");
    public string VerifyBackup => L("verifizieren", "verify");

    // ── Common ───────────────────────────────────────────────────────────────
    public string Days => L("Tage", "days");
    public string Active => L("Aktiv", "Active");
    public string Inactive => L("Inaktiv", "Inactive");
    public string Revoked => L("Gesperrt", "Revoked");

    private string L(string de, string en) => Language == "en" ? en : de;
}
