using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Encodings.Web;
using HomeCA.Service.Resources;
using Microsoft.Extensions.Localization;
using QRCoder;

namespace HomeCA.Service.Trust;

/// <summary>
/// Renders the public trust onboarding page as plain HTML. It works without the interactive UI and over
/// plain HTTP, because a device that does not trust HomeCA yet cannot open the HTTPS interface without warnings.
/// </summary>
public static class TrustPage
{
    public const string Path = "/trust";

    // Umlauts and other letters stay readable in the source; markup characters are still encoded.
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);

    public static string DetectPlatform(string? userAgent)
    {
        var agent = userAgent ?? string.Empty;
        if (agent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || agent.Contains("iPad", StringComparison.OrdinalIgnoreCase)) return "ios";
        if (agent.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "android";
        if (agent.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "windows";
        if (agent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) || agent.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase)) return "macos";
        if (agent.Contains("Linux", StringComparison.OrdinalIgnoreCase) || agent.Contains("X11", StringComparison.OrdinalIgnoreCase)) return "linux";
        return "other";
    }

    /// <summary>The page links to the bootstrap address, preferring plain HTTP so a new device can open it without warnings.</summary>
    public static string Render(X509Certificate2? root, string bootstrapBaseUrl, string? userAgent, IStringLocalizer<UiText> text)
    {
        var html = Encoder;
        string T(string key, params object[] arguments) => html.Encode(text[key, arguments]);
        var baseUrl = bootstrapBaseUrl.TrimEnd('/');
        var platform = DetectPlatform(userAgent);
        var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

        var body = new StringBuilder();
        body.Append($"""<header><picture><source srcset="/brand/logo-dark.png" media="(prefers-color-scheme: dark)"><img src="/brand/logo.png" width="161" height="48" alt="HomeCA"></picture><h1>{T("TrustPageHeading")}</h1><p>{T("TrustPageIntro")}</p></header>""");

        if (root is null)
        {
            body.Append($"""<section class="card"><p>{T("TrustPageNotInitialized")}</p></section>""");
            return Wrap(language, T("TrustPageTitle"), body.ToString());
        }

        var sha256 = Colonize(Convert.ToHexString(SHA256.HashData(root.RawData)));
        var sha1 = Colonize(root.Thumbprint);
        var pageUrl = baseUrl + Path;
        var der = $"{baseUrl}/api/v1/trust-anchor/der";
        var pem = $"{baseUrl}/api/v1/trust-anchor/pem";
        var profile = $"{baseUrl}/api/v1/trust-anchor/mobileconfig";

        body.Append($"""
            <section class="card verify">
              <div>
                <h2>{T("TrustPageVerifyTitle")}</h2>
                <p>{T("TrustPageVerifyText")}</p>
                <dl><dt>{T("TrustPageCertificate")}</dt><dd>{html.Encode(root.Subject)}</dd>
                <dt>{T("ValidUntil")}</dt><dd>{html.Encode(root.NotAfter.ToString("d", CultureInfo.CurrentCulture))}</dd>
                <dt>SHA-256</dt><dd><code>{sha256}</code></dd><dt>SHA-1</dt><dd><code>{sha1}</code></dd></dl>
              </div>
              <figure>{QrSvg(pageUrl)}<figcaption>{T("TrustPageQr")}<br><code>{html.Encode(pageUrl)}</code></figcaption></figure>
            </section>
            <p class="detected">{T("TrustPageDetected", text[$"TrustPagePlatform_{platform}"])}</p>
            """);

        Section("ios", text["TrustPagePlatform_ios"], $"""
            <ol><li>{T("TrustPageIosStep1")}</li><li>{T("TrustPageIosStep2")}</li><li>{T("TrustPageIosStep3")}</li></ol>
            <p><a class="button" href="{html.Encode(profile)}">{T("TrustPageDownloadProfile")}</a></p>
            """);
        Section("android", text["TrustPagePlatform_android"], $"""
            <ol><li>{T("TrustPageAndroidStep1")}</li><li>{T("TrustPageAndroidStep2")}</li><li>{T("TrustPageAndroidStep3")}</li></ol>
            <p><a class="button" href="{html.Encode(der)}" download="homeca-root-ca.crt">{T("TrustPageDownloadCertificate")}</a></p>
            <p class="note">{T("TrustPageAndroidNote")}</p>
            """);
        Section("windows", text["TrustPagePlatform_windows"], $"""
            <p>{T("TrustPageWindowsText")}</p>
            {Code($"curl.exe --fail --silent --show-error --insecure -o $env:TEMP\\homeca-root-ca.cer {der}\nif ((Get-FileHash $env:TEMP\\homeca-root-ca.cer -Algorithm SHA256).Hash -ne '{sha256.Replace(":", string.Empty)}') {{ throw 'Fingerprint mismatch' }}\nImport-Certificate -FilePath $env:TEMP\\homeca-root-ca.cer -CertStoreLocation Cert:\\LocalMachine\\Root", html)}
            <p><a class="button secondary" href="{html.Encode(der)}">{T("TrustPageDownloadCertificate")}</a></p>
            """);
        Section("macos", text["TrustPagePlatform_macos"], $"""
            <p>{T("TrustPageMacText")}</p>
            <p><a class="button" href="{html.Encode(profile)}">{T("TrustPageDownloadProfile")}</a></p>
            {Code($"curl -fsSo /tmp/homeca-root-ca.cer {der}\nsudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain /tmp/homeca-root-ca.cer", html)}
            """);
        Section("linux", text["TrustPagePlatform_linux"], $"""
            <p>Debian / Ubuntu / Proxmox</p>
            {Code($"sudo curl -fsSo /usr/local/share/ca-certificates/homeca-root-ca.crt {pem}\nsudo update-ca-certificates", html)}
            <p>Fedora / RHEL / Rocky</p>
            {Code($"sudo curl -fsSo /etc/pki/ca-trust/source/anchors/homeca-root-ca.pem {pem}\nsudo update-ca-trust", html)}
            """);
        Section("firefox", "Firefox", $"""<p>{T("TrustPageFirefoxText")}</p>""");

        return Wrap(language, T("TrustPageTitle"), body.ToString());

        void Section(string id, string title, string content) =>
            body.Append($"""<details id="{id}"{(id == platform ? " open" : string.Empty)}><summary>{html.Encode(title)}</summary><div class="content">{content}</div></details>""");
    }

    private static string Code(string code, HtmlEncoder html) => $"<pre><code>{html.Encode(code)}</code></pre>";

    private static string Colonize(string hex) => string.Join(':', Enumerable.Range(0, hex.Length / 2).Select(index => hex.Substring(index * 2, 2)));

    private static string QrSvg(string url)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(new System.Drawing.Size(180, 180), "#000000", "#ffffff", drawQuietZones: true, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    private static string Wrap(string language, string title, string body) => $$"""
        <!DOCTYPE html>
        <html lang="{{language}}">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{title}}</title>
        <link rel="icon" href="/brand/favicon.ico">
        <style>
          :root { --bg: #f4f7fb; --surface: #ffffff; --text: #14202e; --muted: #546274; --accent: #024381; --accent-text: #ffffff; --border: #d8e0ea; }
          @media (prefers-color-scheme: dark) { :root { --bg: #0b1320; --surface: #111c2d; --text: #e6edf6; --muted: #9aa8bb; --accent: #4f9be8; --accent-text: #08111d; --border: #23324a; } }
          * { box-sizing: border-box; }
          body { margin: 0; background: var(--bg); color: var(--text); font: 16px/1.5 system-ui, -apple-system, "Segoe UI", sans-serif; }
          main { max-width: 860px; margin: 0 auto; padding: 24px 16px 48px; }
          header img { display: block; margin-bottom: 8px; }
          h1 { font-size: 1.8rem; margin: 0 0 8px; } h2 { font-size: 1.2rem; margin: 0 0 8px; }
          header p, .detected, .note { color: var(--muted); }
          .card, details { background: var(--surface); border: 1px solid var(--border); border-radius: 12px; margin: 16px 0; }
          .card { padding: 20px; }
          .verify { display: flex; gap: 24px; align-items: flex-start; justify-content: space-between; flex-wrap: wrap; }
          .verify > div { flex: 1 1 320px; min-width: 0; }
          dl { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 4px 12px; margin: 12px 0 0; } dt { color: var(--muted); } dd { margin: 0; overflow-wrap: anywhere; }
          figure { margin: 0; text-align: center; flex: 0 0 auto; } figure svg { width: 180px; height: 180px; border-radius: 8px; } figcaption { font-size: .85rem; color: var(--muted); max-width: 220px; overflow-wrap: anywhere; }
          summary { cursor: pointer; padding: 14px 20px; font-weight: 600; } .content { padding: 0 20px 16px; }
          pre { background: var(--bg); border: 1px solid var(--border); border-radius: 8px; padding: 12px; overflow-x: auto; } code { font-family: ui-monospace, "Cascadia Code", Consolas, monospace; font-size: .85rem; }
          .button { display: inline-block; background: var(--accent); color: var(--accent-text); padding: 10px 16px; border-radius: 8px; text-decoration: none; font-weight: 600; }
          .button.secondary { background: transparent; color: var(--accent); border: 1px solid var(--accent); }
        </style>
        </head>
        <body><main>{{body}}</main></body>
        </html>
        """;
}
