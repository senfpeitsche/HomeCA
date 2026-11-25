using HomeCA.Service.Infrastructure;
using HomeCA.Service.Pki;
using HomeCA.Service.Security;

namespace HomeCA.Service.Endpoints;

/// <summary>
/// Scoped issuance API for automated clients (<c>/api/v1/issuance</c>) and its management by administrators
/// (<c>/api/v1/issuance-tokens</c>). The two groups use different filters, so a token never reaches a management endpoint.
/// </summary>
static class IssuanceEndpoints
{
    private const string Tag = "Issuance";

    public static void MapIssuanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var manage = endpoints.MapGroup("/api/v1/issuance-tokens").AddEndpointFilter<BearerTokenFilter>().WithTags("Issuance tokens");
        manage.MapGet("/", async (IssuanceTokenRegistry tokens, CancellationToken ct) => Results.Ok(await tokens.ListAsync(ct)))
            .WithSummary("List issuance tokens (scope, last use, status; never the secret).")
            .Produces<IReadOnlyList<IssuanceToken>>();
        manage.MapPost("/", CreateTokenAsync)
            .WithSummary("Create an issuance token. The secret is returned only in this response.")
            .Produces<CreatedIssuanceToken>().ProducesValidationProblem().Produces(StatusCodes.Status409Conflict);
        manage.MapPost("/{id}/revoke", async (string id, IssuanceTokenRegistry tokens, LocalAdministrationService administration, CancellationToken ct) =>
            {
                if (await tokens.RevokeAsync(id, ct) is not { } token) return Results.NotFound();
                await administration.RecordAuditAsync("issuance-token.revoked", IssuanceTokenFilter.Describe(token), ct);
                return Results.Ok(token);
            })
            .WithSummary("Revoke an issuance token; further requests with it are rejected with 401.")
            .Produces<IssuanceToken>().Produces(StatusCodes.Status404NotFound);

        var issuance = endpoints.MapGroup("/api/v1/issuance")
            .AddEndpointFilter<IssuanceTokenFilter>()
            .AddEndpointFilter(CertificateEndpoints.RejectInvalidCertificateId)
            .WithTags(Tag)
            .WithDescription("Authenticate with `Authorization: Bearer hca_it_…`. Administrator sessions are not accepted; unknown, revoked or expired tokens return 401.");
        issuance.MapPost("/certificates", IssueAsync)
            .WithSummary("Issue a key and certificate within the token scope (no automatic renewal).")
            .Produces<IssueResult>().Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status429TooManyRequests).ProducesValidationProblem();
        issuance.MapPost("/certificates/csr", SignCsrAsync)
            .WithSummary("Sign a CSR; its CN and all SANs must lie within the token scope.")
            .Produces<IssueResult>().Produces(StatusCodes.Status403Forbidden).Produces(StatusCodes.Status429TooManyRequests).ProducesValidationProblem();
        issuance.MapGet("/certificates/{id}", GetAsync)
            .WithSummary("Details of a certificate issued with this token; others return 404.")
            .Produces<CertificateDetails>().Produces(StatusCodes.Status404NotFound);
        issuance.MapPost("/certificates/{id}/export/pfx", ExportPfxAsync)
            .WithSummary("PFX export of a certificate issued with this token; others return 404.")
            .Produces(StatusCodes.Status200OK, contentType: "application/x-pkcs12").Produces(StatusCodes.Status404NotFound).ProducesValidationProblem();
        issuance.MapGet("/certificates/{id}/export/fullchain", ExportFullChainAsync)
            .WithSummary("PEM certificate and issuer chain (no key) of a certificate issued with this token; for CSR certificates, which have no PFX export. Others return 404.")
            .Produces(StatusCodes.Status200OK, contentType: "application/x-pem-file").Produces(StatusCodes.Status404NotFound);
    }

    internal static async Task<IResult> CreateTokenAsync(CreateIssuanceTokenRequest request, HttpContext http, IssuanceTokenRegistry tokens, LocalAdministrationService administration, CancellationToken ct)
    {
        var bearer = http.Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
        var session = await administration.ValidateSessionAsync(bearer, ct);
        try
        {
            var created = await tokens.CreateAsync(request, session.UserName, ct);
            var token = created.Token;
            await administration.RecordAuditAsync("issuance-token.created",
                $"{IssuanceTokenFilter.Describe(token)}: suffixes {string.Join(", ", token.AllowedDnsSuffixes)}; networks {string.Join(", ", token.AllowedIpCidrs)}; max {token.MaxValidityDays} days", ct);
            return Results.Ok(created);
        }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [ex.Message] }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
    }

    internal static async Task<IResult> IssueAsync(IssueCertificateApiRequest request, HttpContext http, CertificateIssuanceService certificates, IssuanceTokenFilter filter, LocalAdministrationService administration, CancellationToken ct)
    {
        var token = CallerToken(http);
        if (!filter.TryAcquireIssuance(token)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        var scope = IssuanceTokenScope.Check(token, request.Usage, request.DnsNames, request.IpAddresses, request.ValidityDays);
        if (!scope.Allowed) return await DenyAsync(token, scope.Violation!, administration, ct);

        IssueResult result;
        try
        {
            // AutoRenew of the management request is ignored on purpose: the client renews by issuing again.
            result = await certificates.IssueAsync(new IssueCertificateRequest(CanonicalUsage(token, request.Usage), scope.DnsNames, scope.IpAddresses,
                request.ValidityDays, request.KeyAlgorithm, request.RsaKeySize, request.TargetProfileId), ct, token.Id);
        }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["certificate"] = [ex.Message] }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        await administration.RecordAuditAsync("issuance-token.issued", $"{IssuanceTokenFilter.Describe(token)}: certificate {result.Id} for {Names(scope)}", ct);
        return Results.Ok(result);
    }

    internal static async Task<IResult> SignCsrAsync(SignCsrRequest request, HttpContext http, CertificateIssuanceService certificates, IssuanceTokenFilter filter, LocalAdministrationService administration, CancellationToken ct)
    {
        var token = CallerToken(http);
        if (!filter.TryAcquireIssuance(token)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        IReadOnlyList<string> csrDns, csrIps;
        try { (csrDns, csrIps) = IssuanceTokenScope.CsrNames(request.CsrPem); }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["csr"] = [ex.Message] }); }

        // Every name in the CSR and in the request must be in scope, even those the signed certificate will not carry.
        var all = IssuanceTokenScope.Check(token, request.Usage, csrDns.Concat(request.DnsNames ?? []), csrIps.Concat(request.IpAddresses ?? []), request.ValidityDays);
        if (!all.Allowed) return await DenyAsync(token, all.Violation!, administration, ct);
        var explicitNames = request.DnsNames is { Count: > 0 } || request.IpAddresses is { Count: > 0 };
        var effective = IssuanceTokenScope.Check(token, request.Usage, explicitNames ? request.DnsNames : csrDns, explicitNames ? request.IpAddresses : csrIps, request.ValidityDays);

        IssueResult result;
        try
        {
            result = await certificates.SignCsrAsync(request with { Usage = CanonicalUsage(token, request.Usage), DnsNames = effective.DnsNames, IpAddresses = effective.IpAddresses }, ct, token.Id);
        }
        catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["csr"] = [ex.Message] }); }
        catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
        await administration.RecordAuditAsync("issuance-token.csr-signed", $"{IssuanceTokenFilter.Describe(token)}: certificate {result.Id} for {Names(effective)}", ct);
        return Results.Ok(result);
    }

    internal static async Task<IResult> GetAsync(string id, HttpContext http, CertificateIssuanceService certificates, CancellationToken ct)
    {
        if (!await OwnsAsync(CallerToken(http), id, certificates, ct)) return Results.NotFound();
        return await certificates.GetDetailsAsync(id, ct) is { } details ? Results.Ok(details) : Results.NotFound();
    }

    internal static async Task<IResult> ExportPfxAsync(string id, PfxExportRequest request, HttpContext http, CertificateIssuanceService certificates, HomeCaStorage storage, LocalAdministrationService administration, CancellationToken ct)
    {
        var token = CallerToken(http);
        if (!await OwnsAsync(token, id, certificates, ct)) return Results.NotFound();
        var result = CertificateEndpoints.ExportPfx(id, request, storage);
        if (result is IStatusCodeHttpResult { StatusCode: StatusCodes.Status200OK } or IFileHttpResult)
            await administration.RecordAuditAsync("issuance-token.exported", $"{IssuanceTokenFilter.Describe(token)}: PFX of certificate {id}", ct);
        return result;
    }

    internal static async Task<IResult> ExportFullChainAsync(string id, HttpContext http, CertificateIssuanceService certificates, HomeCaStorage storage, LocalAdministrationService administration, CancellationToken ct)
    {
        var token = CallerToken(http);
        if (!await OwnsAsync(token, id, certificates, ct)) return Results.NotFound();
        var path = Path.Combine(storage.RootPath, "exports", id, "fullchain.pem");
        if (!File.Exists(path)) return Results.NotFound();
        await administration.RecordAuditAsync("issuance-token.exported", $"{IssuanceTokenFilter.Describe(token)}: full chain of certificate {id}", ct);
        return Results.File(path, "application/x-pem-file", $"{id}-fullchain.pem");
    }

    /// <summary>Foreign certificates look exactly like missing ones, so a token learns nothing about them.</summary>
    private static async Task<bool> OwnsAsync(IssuanceToken token, string id, CertificateIssuanceService certificates, CancellationToken ct) =>
        await certificates.FindAsync(id, ct) is { IssuedByTokenId: { } owner } && owner == token.Id;

    private static async Task<IResult> DenyAsync(IssuanceToken token, string violation, LocalAdministrationService administration, CancellationToken ct)
    {
        await administration.RecordAuditAsync("issuance-token.denied", $"{IssuanceTokenFilter.Describe(token)}: {violation}", ct);
        return Results.Problem(detail: violation, statusCode: StatusCodes.Status403Forbidden);
    }

    private static IssuanceToken CallerToken(HttpContext http) =>
        http.Items[IssuanceTokenFilter.TokenItemKey] as IssuanceToken ?? throw new InvalidOperationException("The issuance token filter did not run.");

    private static string CanonicalUsage(IssuanceToken token, string? usage) =>
        token.AllowedUsages.First(allowed => allowed.Equals(usage, StringComparison.OrdinalIgnoreCase));

    private static string Names(ScopeCheck scope) => string.Join(", ", scope.DnsNames.Concat(scope.IpAddresses));
}
