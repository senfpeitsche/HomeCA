using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security;

namespace HomeCA.Service.Trust;

/// <summary>
/// Builds an unsigned Apple configuration profile (.mobileconfig) that installs the root CA on iOS, iPadOS
/// and macOS. Payload identifiers derive from the certificate fingerprint, so installing the profile again
/// replaces the earlier one instead of adding a duplicate.
/// </summary>
public static class TrustProfile
{
    public const string ContentType = "application/x-apple-aspen-config";

    public static string Build(X509Certificate2 root)
    {
        var fingerprint = Convert.ToHexString(SHA256.HashData(root.RawData)).ToLowerInvariant();
        var name = SecurityElement.Escape(root.GetNameInfo(X509NameType.SimpleName, false)) ?? "HomeCA Root CA";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>PayloadContent</key>
              <array>
                <dict>
                  <key>PayloadCertificateFileName</key><string>homeca-root-ca.cer</string>
                  <key>PayloadContent</key><data>{Convert.ToBase64String(root.RawData)}</data>
                  <key>PayloadDescription</key><string>HomeCA root certificate authority</string>
                  <key>PayloadDisplayName</key><string>{name}</string>
                  <key>PayloadIdentifier</key><string>homeca.root.{fingerprint[..16]}</string>
                  <key>PayloadType</key><string>com.apple.security.root</string>
                  <key>PayloadUUID</key><string>{DerivedUuid(fingerprint, "certificate")}</string>
                  <key>PayloadVersion</key><integer>1</integer>
                </dict>
              </array>
              <key>PayloadDescription</key><string>Trusts certificates issued by this HomeCA instance. SHA-256: {fingerprint}</string>
              <key>PayloadDisplayName</key><string>{name}</string>
              <key>PayloadIdentifier</key><string>homeca.trust.{fingerprint[..16]}</string>
              <key>PayloadRemovalDisallowed</key><false/>
              <key>PayloadType</key><string>Configuration</string>
              <key>PayloadUUID</key><string>{DerivedUuid(fingerprint, "profile")}</string>
              <key>PayloadVersion</key><integer>1</integer>
            </dict>
            </plist>

            """.Replace("\r\n", "\n");
    }

    private static string DerivedUuid(string fingerprint, string purpose)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{purpose}:{fingerprint}"))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50); // version 5 style
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // RFC 4122 variant
        return new Guid(bytes, bigEndian: true).ToString().ToUpperInvariant();
    }
}
