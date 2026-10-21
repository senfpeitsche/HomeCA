using HomeCA.Service.Acme;
using HomeCA.Service.Automation;
using HomeCA.Service.Deployments;
using HomeCA.Service.Endpoints;
using HomeCA.Service.Pki;
using HomeCA.Service.Profiles;
using HomeCA.Service.Revocation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeCA.Tests;

public sealed class AutoRenewalIssuanceTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Theory]
    [InlineData(365, 30)]
    [InlineData(90, 30)]
    [InlineData(30, 10)]
    [InlineData(7, 2)]
    [InlineData(2, 1)]
    public void Default_window_never_reaches_the_validity(int validityDays, int expected)
    {
        Assert.Equal(expected, RenewalPlanRegistry.DefaultRenewBeforeDays(validityDays));
        Assert.True(expected < validityDays);
    }

    [Fact]
    public void One_day_certificates_get_no_default_window() =>
        Assert.Null(RenewalPlanRegistry.DefaultRenewBeforeDays(1));

    [Fact]
    public async Task Issuance_creates_a_renewal_plan_by_default()
    {
        var (certificates, plans) = await SetupAsync();

        var response = await Issue(certificates, plans, new IssueCertificateApiRequest("TLS", ["nas.example.com"], [], 90));

        var result = Assert.IsType<Ok<IssueResult>>(response).Value!;
        var plan = Assert.Single(await plans.ListAsync(CancellationToken.None));
        Assert.Equal(plan.Id, result.RenewalPlanId);
        Assert.Equal(result.Id, plan.CertificateId);
        Assert.Equal(30, plan.RenewBeforeDays);
        Assert.True(plan.Enabled);
    }

    [Fact]
    public async Task Issuance_without_auto_renew_creates_no_plan()
    {
        var (certificates, plans) = await SetupAsync();

        var response = await Issue(certificates, plans, new IssueCertificateApiRequest("TLS", ["lab.example.com"], [], 90, AutoRenew: false));

        Assert.Null(Assert.IsType<Ok<IssueResult>>(response).Value!.RenewalPlanId);
        Assert.Empty(await plans.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Explicit_window_is_used_and_validated_before_issuance()
    {
        var (certificates, plans) = await SetupAsync();

        var rejected = await Issue(certificates, plans, new IssueCertificateApiRequest("TLS", ["bad.example.com"], [], 30, RenewBeforeDays: 30));
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<ProblemHttpResult>(rejected).StatusCode);
        Assert.Empty(await certificates.ListAsync(CancellationToken.None));

        await Issue(certificates, plans, new IssueCertificateApiRequest("TLS", ["ok.example.com"], [], 30, RenewBeforeDays: 14));
        Assert.Equal(14, Assert.Single(await plans.ListAsync(CancellationToken.None)).RenewBeforeDays);
    }

    private static Task<IResult> Issue(CertificateIssuanceService certificates, RenewalPlanRegistry plans, IssueCertificateApiRequest request) =>
        CertificateEndpoints.IssueWithRenewalAsync(request, certificates, plans, NullLogger.Instance, CancellationToken.None);

    private async Task<(CertificateIssuanceService Certificates, RenewalPlanRegistry Plans)> SetupAsync()
    {
        var storage = _fixture.CreateStorage();
        var authorities = new CertificateAuthorityService(storage, NullLogger<CertificateAuthorityService>.Instance);
        await authorities.InitializeAsync(CancellationToken.None);
        var deployments = new DeploymentPackageService(new TargetProfileRegistry(storage), NullLogger<DeploymentPackageService>.Instance);
        var revocations = new RevocationRegistry(storage, NullLogger<RevocationRegistry>.Instance);
        var crl = new CrlService(storage, revocations, authorities, NullLogger<CrlService>.Instance);
        var certificates = new CertificateIssuanceService(storage, deployments, authorities, revocations, crl, _fixture.CreateOptions(), NullLogger<CertificateIssuanceService>.Instance);
        return (certificates, new RenewalPlanRegistry(storage, NullLogger<RenewalPlanRegistry>.Instance));
    }

    public void Dispose() => _fixture.Dispose();
}
