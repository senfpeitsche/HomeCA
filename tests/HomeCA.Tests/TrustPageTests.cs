using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using HomeCA.Service.Infrastructure;
using HomeCA.Service.Resources;
using HomeCA.Service.Trust;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeCA.Tests;

public sealed class TrustPageTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X)", "ios")]
    [InlineData("Mozilla/5.0 (Linux; Android 15; Pixel 9)", "android")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64)", "windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6)", "macos")]
    [InlineData("Mozilla/5.0 (X11; Ubuntu; Linux x86_64)", "linux")]
    [InlineData(null, "other")]
    public void Platform_is_detected_from_the_user_agent(string? userAgent, string expected) =>
        Assert.Equal(expected, TrustPage.DetectPlatform(userAgent));

    [Fact]
    public void Page_shows_fingerprint_qr_code_and_opens_the_detected_platform()
    {
        using var root = CreateRoot();
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de");
        try
        {
            var html = TrustPage.Render(root, "http://ca.lab.test:5080/", "Mozilla/5.0 (Linux; Android 15)", Localizer());

            var fingerprint = string.Join(':', Convert.ToHexString(SHA256.HashData(root.RawData)).Chunk(2).Select(pair => new string(pair)));
            Assert.Contains(fingerprint, html);
            Assert.Contains("<svg", html);
            Assert.Contains("<details id=\"android\" open>", html);
            Assert.Contains("<details id=\"ios\">", html);
            Assert.Contains("http://ca.lab.test:5080/api/v1/trust-anchor/mobileconfig", html);
            Assert.Contains("lang=\"de\"", html);
            Assert.Contains("Fingerprint prüfen", html);
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void Page_explains_a_missing_root_ca()
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en");
        try { Assert.Contains("no root CA", TrustPage.Render(null, "http://ca.lab.test", null, Localizer())); }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Fact]
    public void Apple_profile_contains_the_root_certificate_with_stable_identifiers()
    {
        using var root = CreateRoot();

        var first = XDocument.Parse(TrustProfile.Build(root));
        var second = TrustProfile.Build(root);

        var data = first.Descendants("data").Single().Value;
        Assert.Equal(root.RawData, Convert.FromBase64String(data));
        Assert.Contains("com.apple.security.root", first.ToString());
        Assert.Equal(first.ToString(), XDocument.Parse(second).ToString());
    }

    [Theory]
    [InlineData("/trust", true)]
    [InlineData("/brand/logo.png", true)]
    [InlineData("/trustworthy", false)]
    public void Trust_page_and_its_logo_stay_on_plain_http(string path, bool allowed) =>
        Assert.Equal(allowed, PlainHttpPolicy.IsAllowedOverPlainHttp(new PathString(path)));

    private static X509Certificate2 CreateRoot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=HomeCA Test Root", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadCertificate(certificate.RawData);
    }

    private static IStringLocalizer<UiText> Localizer() =>
        new StringLocalizer<UiText>(new ResourceManagerStringLocalizerFactory(Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));
}
