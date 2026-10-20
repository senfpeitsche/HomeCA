using HomeCA.Service.Acme;
using HomeCA.Service.Connectors;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class ExternalAcmeServiceTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task Cleanup_Removes_Records_Without_A_Cancelled_Request_Token()
    {
        // The connector throws on a cancelled token; cleanup runs on its own timeout instead of the request token.
        var dns = new RecordingDnsConnector();
        var service = CreateService(dns);

        await service.CleanupTxtRecordsAsync(dns, new ConnectorSettings("dns", dns.Type, new Dictionary<string, string>()),
            [("_acme-challenge.a.example.test", "one"), ("_acme-challenge.b.example.test", "two")]);

        Assert.Equal(["_acme-challenge.a.example.test", "_acme-challenge.b.example.test"], dns.Deleted);
    }

    [Fact]
    public async Task Cleanup_Continues_After_A_Failing_Record()
    {
        var dns = new RecordingDnsConnector { FailFor = "_acme-challenge.a.example.test" };
        var service = CreateService(dns);

        await service.CleanupTxtRecordsAsync(dns, new ConnectorSettings("dns", dns.Type, new Dictionary<string, string>()),
            [("_acme-challenge.a.example.test", "one"), ("_acme-challenge.b.example.test", "two")]);

        Assert.Equal(["_acme-challenge.b.example.test"], dns.Deleted);
    }

    [Fact]
    public void Failed_Validation_Message_Contains_The_Server_Reasons()
    {
        Assert.Equal("ACME order validation failed: _acme-challenge.a.example.test: No TXT record found",
            ExternalAcmeService.DescribeFailedValidation(["_acme-challenge.a.example.test: No TXT record found"]));
        Assert.Contains("Check DNS records", ExternalAcmeService.DescribeFailedValidation([]));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(601)]
    public async Task Rejects_Out_Of_Range_Propagation_Delay(int seconds)
    {
        var service = CreateService(new RecordingDnsConnector());

        await Assert.ThrowsAsync<ArgumentException>(() => service.RequestCertificateAsync(
            new ExternalAcmeOrderRequest("issuer", ["a.example.test"], seconds), CancellationToken.None));
    }

    [Fact]
    public async Task Listing_Skips_Damaged_Metadata()
    {
        var service = CreateService(new RecordingDnsConnector());
        var root = Path.Combine(_fixture.RootPath, "external-certificates");
        Directory.CreateDirectory(Path.Combine(root, "broken"));
        File.WriteAllText(Path.Combine(root, "broken", "metadata.json"), "{ not json");
        Directory.CreateDirectory(Path.Combine(root, "good"));
        File.WriteAllText(Path.Combine(root, "good", "metadata.json"),
            System.Text.Json.JsonSerializer.Serialize(new ExternalAcmeOrderMetadata("good", "LE", ["a.example.test"], DateTimeOffset.UtcNow, "/x")));

        var item = Assert.Single(await service.ListCertificatesAsync(CancellationToken.None));

        Assert.Equal("good", item.Id);
    }

    private ExternalAcmeService CreateService(IDnsConnector dns)
    {
        var storage = _fixture.CreateStorage();
        var catalog = new ConnectorCatalog([dns]);
        return new ExternalAcmeService(storage, new ExternalAcmeIssuerRegistry(storage, NullLogger<ExternalAcmeIssuerRegistry>.Instance),
            new ConnectorRegistry(storage, catalog), catalog, NullLogger<ExternalAcmeService>.Instance);
    }

    private sealed class RecordingDnsConnector : IDnsConnector
    {
        public string Type => "recording";
        public string? FailFor { get; init; }
        public List<string> Deleted { get; } = [];

        public Task<ConnectorCheckResult> CheckAsync(ConnectorSettings settings, CancellationToken cancellationToken) =>
            Task.FromResult(new ConnectorCheckResult(true, []));

        public Task UpsertTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task DeleteTxtRecordAsync(ConnectorSettings settings, string recordName, string value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (recordName == FailFor) throw new HttpRequestException("DNS API unavailable");
            Deleted.Add(recordName);
            return Task.CompletedTask;
        }
    }

    public void Dispose() => _fixture.Dispose();
}
