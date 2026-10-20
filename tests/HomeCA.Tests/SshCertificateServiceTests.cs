using System.Diagnostics;
using HomeCA.Service.Pki;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

/// <summary>
/// Runs a test that needs ssh-keygen. Locally it is reported as skipped when ssh-keygen is missing;
/// on CI (CI=true) it always runs, so a missing tool fails the build instead of passing silently.
/// </summary>
public sealed class SshKeygenFactAttribute : FactAttribute
{
    public SshKeygenFactAttribute()
    {
        if (!SshKeygen.IsAvailable && !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase))
            Skip = "ssh-keygen is not installed; the SSH CA depends on it at runtime.";
    }
}

internal static class SshKeygen
{
    public static bool IsAvailable { get; } = Probe();

    public static int Run(params string[] arguments)
    {
        var start = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to start ssh-keygen.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Task.WaitAll(output, error);
        return process.ExitCode;
    }

    private static bool Probe()
    {
        try
        {
            // Generating a throwaway key proves the binary exists and works.
            var path = Path.Combine(Path.GetTempPath(), $"homeca-probe-{Guid.NewGuid():N}");
            var exitCode = Run("-q", "-t", "ed25519", "-N", "", "-f", path);
            File.Delete(path);
            File.Delete(path + ".pub");
            return exitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

public sealed class SshCertificateServiceTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [SshKeygenFact]
    public async Task Revoking_A_Second_Certificate_Keeps_The_First_One_Revoked()
    {
        var service = CreateServiceWithAuthority("ssh-user");

        var first = await service.IssueAsync(new SshIssueRequest("user", "first", ["alice"], CreateUserPublicKey("first")), CancellationToken.None);
        var second = await service.IssueAsync(new SshIssueRequest("user", "second", ["bob"], CreateUserPublicKey("second")), CancellationToken.None);

        Assert.True(await service.DeleteAsync(first.Id, CancellationToken.None));
        Assert.True(await service.DeleteAsync(second.Id, CancellationToken.None));

        var krlPath = Path.Combine(_fixture.RootPath, "authorities", "ssh-user", "revoked.krl");
        Assert.True(IsRevoked(krlPath, first.CertificatePath), "The first certificate must stay revoked after a second revocation.");
        Assert.True(IsRevoked(krlPath, second.CertificatePath));
    }

    [SshKeygenFact]
    public async Task List_Reports_Kind_Identity_And_All_Principals()
    {
        var service = CreateServiceWithAuthority("ssh-host");
        // ECDSA certificate types do not start with "ssh-"; they must still be recognized as host certificates.
        var issued = await service.IssueAsync(new SshIssueRequest("host", "web01", ["web01.example.test", "10.0.0.5"], CreateUserPublicKey("host", "ecdsa")), CancellationToken.None);

        var item = Assert.Single(await service.ListAsync(CancellationToken.None));

        Assert.Equal(issued.Id, item.Id);
        Assert.Equal("host", item.Kind);
        Assert.Equal("web01", item.Identity);
        Assert.Equal("web01.example.test,10.0.0.5", item.Principals);
        Assert.StartsWith("from ", item.ValidUntil);
    }

    [Fact]
    public void Parser_Joins_Multi_Line_Fields()
    {
        const string output = """
            /tmp/x-cert.pub:
                    Type: ecdsa-sha2-nistp256-cert-v01@openssh.com host certificate
                    Key ID: "web01"
                    Serial: 7
                    Valid: from 2026-10-21T10:00:00 to 2027-10-21T10:00:00
                    Principals:
                            web01.example.test
                            10.0.0.5
                    Critical Options: (none)
                    Extensions: (none)
            """;

        var fields = SshCertificateService.ParseCertificateFields(output);

        Assert.Equal("web01.example.test,10.0.0.5", fields["Principals"]);
        Assert.Equal("\"web01\"", fields["Key ID"]);
        Assert.Equal("(none)", fields["Extensions"]);
        Assert.Contains("host certificate", fields["Type"]);
    }

    private SshCertificateService CreateServiceWithAuthority(string authority)
    {
        var caDirectory = Path.Combine(_fixture.RootPath, "authorities", authority);
        Directory.CreateDirectory(caDirectory);
        Assert.Equal(0, SshKeygen.Run("-q", "-t", "ed25519", "-N", "", "-f", Path.Combine(caDirectory, "ca")));
        return new SshCertificateService(_fixture.CreateStorage(), NullLogger<SshCertificateService>.Instance);
    }

    private string CreateUserPublicKey(string name, string type = "ed25519")
    {
        var path = Path.Combine(_fixture.RootPath, $"key-{name}");
        Assert.Equal(0, SshKeygen.Run("-q", "-t", type, "-N", "", "-f", path));
        return File.ReadAllText(path + ".pub");
    }

    // ssh-keygen -Q exits with a non-zero code when the key is listed in the KRL.
    private static bool IsRevoked(string krlPath, string certificatePath) =>
        SshKeygen.Run("-Q", "-f", krlPath, certificatePath) != 0;

    public void Dispose() => _fixture.Dispose();
}
