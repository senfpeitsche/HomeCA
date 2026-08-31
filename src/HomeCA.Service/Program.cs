using HomeCA.Service.Infrastructure;
using HomeCA.Service.Security;
using HomeCA.Service.Pki;
using HomeCA.Service.Domains;
using HomeCA.Service.Profiles;
using HomeCA.Service.Connectors;
using HomeCA.Service.Acme;
using HomeCA.Service.Operations;
using HomeCA.Service.Revocation;
using HomeCA.Service.Deployments;
using HomeCA.Service.Automation;
using HomeCA.Service.Components;
using System.Reflection;
using Microsoft.Extensions.Options;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<HomeCaStorageOptions>()
    .Bind(builder.Configuration.GetSection(HomeCaStorageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddSingleton<HomeCaStorage>();
builder.Services.AddSingleton<SetupStateService>();
builder.Services.AddSingleton<LocalAdministrationService>();
builder.Services.AddSingleton<CertificateAuthorityService>();
builder.Services.AddSingleton<CertificateIssuanceService>();
builder.Services.AddSingleton<SshCertificateService>();
builder.Services.AddSingleton<DomainRegistry>();
builder.Services.AddSingleton<TargetProfileRegistry>();
builder.Services.AddSingleton<DeploymentPackageService>();
builder.Services.AddSingleton<RenewalPlanRegistry>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IDnsConnector, TechnitiumDnsConnector>();
builder.Services.AddSingleton<IDnsConnector, HetznerDnsConnector>();
builder.Services.AddSingleton<ConnectorCatalog>();
builder.Services.AddSingleton<ConnectorRegistry>();
builder.Services.AddSingleton<InternalAcmeService>();
builder.Services.AddSingleton<ExternalAcmeIssuerRegistry>();
builder.Services.AddSingleton<ExternalAcmeService>();
builder.Services.AddSingleton<Rfc8555AcmeService>();
builder.Services.AddSingleton<CertificateExpiryService>();
builder.Services.AddSingleton<RevocationRegistry>();
builder.Services.AddSingleton<CrlService>();
builder.Services.AddSingleton<BearerTokenFilter>();
builder.Services.AddSingleton<LoginRateLimiter>();
builder.Services.AddSingleton<UpdateCheckService>();
builder.Services.AddScoped<UiStrings>();
builder.Services.AddHostedService<RenewalBackgroundService>();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info.Title = "HomeCA API";
        document.Info.Version = "v1";
        document.Info.Description = "Self-hosted homelab PKI — manage CAs, issue TLS/SSH certificates, handle ACME flows, and distribute trust anchors.";
        return Task.CompletedTask;
    });
});
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddMudServices();

var app = builder.Build();

// ── Load persisted PublicUrl from /etc/homeca/public-url.conf if available ──
{
    const string publicUrlPath = "/etc/homeca/public-url.conf";
    if (File.Exists(publicUrlPath))
    {
        var savedUrl = File.ReadAllText(publicUrlPath).Trim();
        if (!string.IsNullOrEmpty(savedUrl))
        {
            var opts = app.Services.GetRequiredService<IOptions<HomeCaStorageOptions>>();
            opts.Value.PublicUrl = savedUrl;
        }
    }
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapOpenApi();

// ── Ensure default administrator exists ─────────────────────────────────────

using (var scope = app.Services.CreateScope())
{
    var administration = scope.ServiceProvider.GetRequiredService<LocalAdministrationService>();
    await administration.EnsureDefaultAdministratorAsync(CancellationToken.None);
}

// ── Public endpoints (no authentication) ────────────────────────────────────

app.MapHealthChecks("/health");
app.MapGet("/api/v1/setup/phase", (SetupStateService setupState) =>
    Results.Ok(new { phase = setupState.Current.SetupPhase.ToString().ToLowerInvariant() }));
app.MapGet("/api/v1/version", () =>
{
    var assembly = Assembly.GetEntryAssembly()!;
    var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    var version = informational.Split('+')[0];
    var commit = informational.Contains('+') ? informational.Split('+')[1] : null;
    return Results.Ok(new { version, commit, runtime = Environment.Version.ToString() });
});
app.MapGet("/api/v1/update-check", async (UpdateCheckService updateCheck, CancellationToken ct) =>
    Results.Ok(await updateCheck.CheckAsync(ct)));
app.MapGet("/api/v1/instance", (HomeCaStorage storage) =>
{
    var informational = Assembly.GetEntryAssembly()!
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    var version = informational.Split('+')[0];
    return Results.Ok(new { version, instance = storage.Describe() });
});
app.MapGet("/api/v1/trust-anchor", async (CertificateAuthorityService authorities, CancellationToken ct) =>
{
    var info = await authorities.GetTrustAnchorInfoAsync(ct);
    return info is null ? Results.NotFound(new { detail = "No active root CA found. Initialize the PKI first." }) : Results.Ok(info);
});
app.MapGet("/api/v1/trust-anchor/pem", async (CertificateAuthorityService authorities, CancellationToken ct) =>
{
    var export = await authorities.GetTrustAnchorAsync("pem", ct);
    return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName);
});
app.MapGet("/api/v1/trust-anchor/der", async (CertificateAuthorityService authorities, CancellationToken ct) =>
{
    var export = await authorities.GetTrustAnchorAsync("der", ct);
    return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName);
});
app.MapGet("/api/v1/crl/latest", async (CrlService crl, CancellationToken ct) =>
{
    var export = await crl.GetLatestAsync(ct);
    return export is null ? Results.NotFound(new { detail = "No CRL has been generated yet. Generate one first via POST /api/v1/crl." }) : Results.File(export.Content, "application/pkix-crl", export.FileName);
});

// ── Unauthenticated management endpoints ────────────────────────────────────

app.MapPost("/api/v1/setup", async (SetupRequest request, HttpContext context, LocalAdministrationService administration, CancellationToken ct) =>
{
    if (context.Connection.RemoteIpAddress is not null && !System.Net.IPAddress.IsLoopback(context.Connection.RemoteIpAddress))
        return Results.Forbid();
    if (string.IsNullOrWhiteSpace(request.UserName) || request.Password.Length < 16)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["credentials"] = ["Username is required and password must be at least 16 characters."] });
    return await administration.SetupAsync(request, ct) ? Results.NoContent() : Results.Conflict();
});
app.MapPost("/api/v1/login", async (LoginRequest request, HttpContext context, LocalAdministrationService administration, LoginRateLimiter rateLimiter, CancellationToken ct) =>
{
    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    if (rateLimiter.IsBlocked(ip))
        return Results.Problem("Too many failed login attempts. Try again later.", statusCode: 429);
    var loginResponse = await administration.LoginAsync(request, ct);
    if (loginResponse is null) { rateLimiter.RecordFailure(ip); return Results.Unauthorized(); }
    rateLimiter.RecordSuccess(ip);
    return Results.Ok(new { accessToken = loginResponse.AccessToken, expiresInSeconds = loginResponse.ExpiresInSeconds, mustChangePassword = loginResponse.MustChangePassword });
});

// ── Unauthenticated ACME client endpoints ───────────────────────────────────

app.MapGet("/api/v1/acme/directory", async (InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.GetDirectoryAsync(ct)));
app.MapPost("/api/v1/acme/accounts", async (RegisterAcmeAccountRequest request, InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.RegisterAccountAsync(request, ct)));
app.MapGet("/api/v1/connectors", (ConnectorCatalog catalog) => Results.Ok(catalog.Types));

// ── RFC 8555-compliant ACME endpoints (standard protocol for acme.sh, certbot, OPNsense) ──
{
    string AcmeBaseUrl(HttpRequest req)
    {
        var scheme = req.Scheme;
        var host = req.Host.ToString();
        return $"{scheme}://{host}";
    }

    IResult AcmeProblemResult(AcmeProblemException ex) => Results.Json(
        new { type = $"urn:ietf:params:acme:error:{ex.ProblemType}", detail = ex.Message, status = ex.StatusCode },
        statusCode: ex.StatusCode, contentType: "application/problem+json");

    // Directory
    app.MapGet("/acme/directory", (HttpRequest req, Rfc8555AcmeService acme) =>
    {
        var b = AcmeBaseUrl(req);
        return Results.Json(new
        {
            newNonce = $"{b}/acme/new-nonce",
            newAccount = $"{b}/acme/new-acct",
            newOrder = $"{b}/acme/new-order",
            revokeCert = $"{b}/acme/revoke-cert",
            keyChange = $"{b}/acme/key-change",
            meta = new { termsOfService = $"{b}/terms", website = b }
        });
    });

    // newNonce
    app.MapMethods("/acme/new-nonce", ["HEAD", "GET"], (HttpContext ctx, Rfc8555AcmeService acme) =>
    {
        ctx.Response.Headers["Replay-Nonce"] = acme.CreateNonce();
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.StatusCode = 200;
        return Results.Ok();
    });

    // Helper: add standard ACME headers to every response.
    void AddAcmeHeaders(HttpContext ctx, Rfc8555AcmeService acme, string? locationUrl = null)
    {
        ctx.Response.Headers["Replay-Nonce"] = acme.CreateNonce();
        ctx.Response.Headers["Cache-Control"] = "no-store";
        if (locationUrl is not null)
            ctx.Response.Headers["Location"] = locationUrl;
    }

    // newAccount
    app.MapPost("/acme/new-acct", async (HttpContext ctx, Rfc8555AcmeService acme, ILogger<Program> logger, CancellationToken ct) =>
    {
        try
        {
            var body = await ReadBodyAsync(ctx.Request);
            logger.LogInformation("ACME newAccount: received {Bytes} bytes from {Remote}", body.Length, ctx.Connection.RemoteIpAddress);
            var expectedUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/new-acct";
            var jws = acme.VerifyJws(body, expectedUrl);

            if (jws.Jwk is null) return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("malformed", "newAccount requires 'jwk' in protected header, not 'kid'."));

            // Parse payload.
            string? contact = null;
            bool onlyReturnExisting = false;
            if (jws.Payload.Length > 0)
            {
                var payload = System.Text.Json.Nodes.JsonNode.Parse(jws.Payload)?.AsObject();
                if (payload is not null)
                {
                    var contacts = payload["contact"]?.AsArray();
                    if (contacts is not null && contacts.Count > 0)
                        contact = contacts[0]?.GetValue<string>();
                    onlyReturnExisting = payload["onlyReturnExisting"]?.GetValue<bool>() ?? false;
                }
            }

            if (onlyReturnExisting)
            {
                var existing = await acme.FindAccountByThumbprintAsync(jws.Thumbprint!, ct);
                if (existing is null)
                    return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("accountDoesNotExist", "No account found for this key."));

                var existingUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/acct/{existing.Id}";
                AddAcmeHeaders(ctx, acme, existingUrl);
                return Results.Json(new { status = existing.Status, contact = existing.Contact, orders = $"{AcmeBaseUrl(ctx.Request)}/acme/acct/{existing.Id}/orders" }, statusCode: 200);
            }

            var account = await acme.NewAccountAsync(jws.Jwk, contact, ct);
            var accountUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/acct/{account.Id}";
            AddAcmeHeaders(ctx, acme, accountUrl);
            logger.LogInformation("ACME newAccount: registered/found account {AccountId}, url={Url}", account.Id, accountUrl);
            return Results.Json(new { status = account.Status, contact = account.Contact, orders = $"{AcmeBaseUrl(ctx.Request)}/acme/acct/{account.Id}/orders" }, statusCode: 201);
        }
        catch (AcmeProblemException ex) { logger.LogWarning("ACME newAccount error: {Type} — {Detail}", ex.ProblemType, ex.Message); AddAcmeHeaders(ctx, acme); return AcmeProblemResult(ex); }
        catch (Exception ex) { logger.LogError(ex, "ACME newAccount internal error"); AddAcmeHeaders(ctx, acme); return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", ex.Message, 500)); }
    });

    // Account lookup (POST-as-GET)
    app.MapPost("/acme/acct/{accountId}", async (string accountId, HttpContext ctx, Rfc8555AcmeService acme, ILogger<Program> logger, CancellationToken ct) =>
    {
        try
        {
            var body = await ReadBodyAsync(ctx.Request);
            var expectedUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/acct/{accountId}";
            var jws = acme.VerifyJws(body, expectedUrl);

            var account = await acme.FindAccountByKidAsync(jws.Kid ?? $"{AcmeBaseUrl(ctx.Request)}/acme/acct/{accountId}", ct);
            if (account is null) return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("accountDoesNotExist", "Account not found."));

            AddAcmeHeaders(ctx, acme, expectedUrl);
            return Results.Json(new { status = account.Status, contact = account.Contact, orders = $"{AcmeBaseUrl(ctx.Request)}/acme/acct/{account.Id}/orders" });
        }
        catch (AcmeProblemException ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(ex); }
        catch (Exception ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", ex.Message, 500)); }
    });

    // newOrder
    app.MapPost("/acme/new-order", async (HttpContext ctx, Rfc8555AcmeService acme, ILogger<Program> logger, CancellationToken ct) =>
    {
        try
        {
            var body = await ReadBodyAsync(ctx.Request);
            logger.LogInformation("ACME newOrder: received {Bytes} bytes from {Remote}", body.Length, ctx.Connection.RemoteIpAddress);
            var expectedUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/new-order";
            var jws = acme.VerifyJws(body, expectedUrl);

            // Resolve account from kid.
            var account = jws.Kid is not null ? await acme.FindAccountByKidAsync(jws.Kid, ct) : null;
            if (account is null) return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("unauthorized", "Account not found. Use 'kid' in protected header."));

            // Verify signature with stored key.
            var storedJwk = System.Text.Json.Nodes.JsonNode.Parse(account.JwkJson)?.AsObject()!;
            var alg = jws.ProtectedHeader["alg"]?.GetValue<string>() ?? "ES256";
            acme.VerifySignatureWithStoredKey(storedJwk, alg, body);

            var payload = System.Text.Json.Nodes.JsonNode.Parse(jws.Payload)?.AsObject()
                ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "Order payload is required.");

            var identifiers = payload["identifiers"]?.AsArray()
                ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "Missing 'identifiers' in order payload.");

            var rfcIdentifiers = identifiers.Select(i =>
            {
                var obj = i?.AsObject();
                return new Rfc8555Identifier(
                    obj?["type"]?.GetValue<string>() ?? "dns",
                    obj?["value"]?.GetValue<string>() ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "Identifier value is required."));
            }).ToList();

            var order = await acme.NewOrderAsync(account.Id, rfcIdentifiers, ct);
            var b = AcmeBaseUrl(ctx.Request);
            var orderUrl = $"{b}/acme/order/{order.Id}";
            AddAcmeHeaders(ctx, acme, orderUrl);

            return Results.Json(FormatOrder(order, b), statusCode: 201);
        }
        catch (AcmeProblemException ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(ex); }
        catch (Exception ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", ex.Message, 500)); }
    });

    // Order (POST-as-GET)
    app.MapPost("/acme/order/{orderId}", async (string orderId, HttpContext ctx, Rfc8555AcmeService acme, CancellationToken ct) =>
    {
        try
        {
            var body = await ReadBodyAsync(ctx.Request);
            var expectedUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/order/{orderId}";
            var jws = acme.VerifyJws(body, expectedUrl);

            var order = await acme.GetOrderAsync(orderId, ct);
            if (order is null) return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("malformed", "Order not found.", 404));

            AddAcmeHeaders(ctx, acme, expectedUrl);
            return Results.Json(FormatOrder(order, AcmeBaseUrl(ctx.Request)));
        }
        catch (AcmeProblemException ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(ex); }
        catch (Exception ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", ex.Message, 500)); }
    });

    // Authorization (POST-as-GET)
    app.MapPost("/acme/authz/{authzId}", async (string authzId, HttpContext ctx, Rfc8555AcmeService acme, ILogger<Program> logger, CancellationToken ct) =>
    {
        try
        {
            var body = await ReadBodyAsync(ctx.Request);
            logger.LogInformation("ACME authz: {AuthzId} from {Remote}", authzId, ctx.Connection.RemoteIpAddress);
            var expectedUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/authz/{authzId}";
            var jws = acme.VerifyJws(body, expectedUrl);

            var authz = await acme.GetAuthorizationAsync(authzId, ct);
            if (authz is null) return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("malformed", "Authorization not found.", 404));

            logger.LogInformation("ACME authz {AuthzId}: identifier={Value}, status={Status}", authzId, authz.Identifier.Value, authz.Status);
            var b = AcmeBaseUrl(ctx.Request);
            AddAcmeHeaders(ctx, acme);
            return Results.Json(new
            {
                identifier = new { type = authz.Identifier.Type, value = authz.Identifier.Value },
                status = authz.Status,
                expires = authz.Expires.ToString("o"),
                challenges = authz.Challenges.Select(c => new
                {
                    type = c.Type,
                    status = c.Status,
                    url = $"{b}/acme/chall/{c.Id}",
                    token = c.Token
                })
            });
        }
        catch (AcmeProblemException ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(ex); }
        catch (Exception ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", ex.Message, 500)); }
    });

    // Challenge (respond to / POST-as-GET)
    app.MapPost("/acme/chall/{challengeId}", async (string challengeId, HttpContext ctx, Rfc8555AcmeService acme, ILogger<Program> logger, CancellationToken ct) =>
    {
        try
        {
            var body = await ReadBodyAsync(ctx.Request);
            logger.LogInformation("ACME challenge: {ChallengeId}, payload={PayloadLen} bytes from {Remote}", challengeId, body.Length, ctx.Connection.RemoteIpAddress);
            var expectedUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/chall/{challengeId}";
            var jws = acme.VerifyJws(body, expectedUrl);

            Rfc8555Challenge? challenge;
            if (jws.Payload.Length > 0)
            {
                // Client is responding to the challenge — auto-approve it.
                challenge = await acme.RespondToChallengeAsync(challengeId, ct);
            }
            else
            {
                // POST-as-GET — just return current status.
                challenge = await acme.GetChallengeAsync(challengeId, ct);
            }
            if (challenge is null) return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("malformed", "Challenge not found.", 404));

            logger.LogInformation("ACME challenge {ChallengeId}: status={Status}", challengeId, challenge.Status);
            AddAcmeHeaders(ctx, acme);
            var result = new Dictionary<string, object?>
            {
                ["type"] = challenge.Type,
                ["status"] = challenge.Status,
                ["url"] = expectedUrl,
                ["token"] = challenge.Token
            };
            if (challenge.ValidatedAt is not null)
                result["validated"] = challenge.ValidatedAt.Value.ToString("o");
            return Results.Json(result);
        }
        catch (AcmeProblemException ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(ex); }
        catch (Exception ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", ex.Message, 500)); }
    });

    // Finalize
    app.MapPost("/acme/order/{orderId}/finalize", async (string orderId, HttpContext ctx, Rfc8555AcmeService acme, ILogger<Program> logger, CancellationToken ct) =>
    {
        try
        {
            var body = await ReadBodyAsync(ctx.Request);
            logger.LogInformation("ACME finalize: order {OrderId} from {Remote}", orderId, ctx.Connection.RemoteIpAddress);
            var expectedUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/order/{orderId}/finalize";
            var jws = acme.VerifyJws(body, expectedUrl);

            var payload = System.Text.Json.Nodes.JsonNode.Parse(jws.Payload)?.AsObject()
                ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "Finalize payload is required.");

            var csrB64 = payload["csr"]?.GetValue<string>()
                ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "Missing 'csr' in finalize payload.");

            var csrDer = Rfc8555AcmeService.Base64UrlDecode(csrB64);
            var order = await acme.FinalizeOrderAsync(orderId, csrDer, ct);

            var b = AcmeBaseUrl(ctx.Request);
            var orderUrl = $"{b}/acme/order/{order.Id}";
            AddAcmeHeaders(ctx, acme, orderUrl);

            var formatted = FormatOrder(order, b);
            var jsonForLog = System.Text.Json.JsonSerializer.Serialize(formatted);
            logger.LogInformation("ACME finalize response body: {Json}", jsonForLog);
            logger.LogInformation("ACME finalize response: order {OrderId}, status={Status}, certId={CertId}, certUrl={CertUrl}",
                order.Id, order.Status, order.CertificateId ?? "null",
                order.CertificateId is not null ? $"{b}/acme/cert/{order.CertificateId}" : "null");
            return Results.Json(formatted);
        }
        catch (AcmeProblemException ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(ex); }
        catch (Exception ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", ex.Message, 500)); }
    });

    // Certificate download (POST-as-GET)
    app.MapPost("/acme/cert/{certificateId}", async (string certificateId, HttpContext ctx, Rfc8555AcmeService acme, ILogger<Program> logger, CancellationToken ct) =>
    {
        try
        {
            var body = await ReadBodyAsync(ctx.Request);
            logger.LogInformation("ACME cert download: {CertificateId} from {Remote}", certificateId, ctx.Connection.RemoteIpAddress);
            var expectedUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/cert/{certificateId}";
            var jws = acme.VerifyJws(body, expectedUrl);

            var pem = await acme.GetCertificatePemAsync(certificateId, ct);
            if (pem is null) { logger.LogWarning("ACME cert {CertificateId}: not found", certificateId); return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("malformed", "Certificate not found.", 404)); }

            logger.LogInformation("ACME cert {CertificateId}: returning {Len} bytes PEM", certificateId, pem.Length);
            AddAcmeHeaders(ctx, acme);
            return Results.Text(pem, "application/pem-certificate-chain");
        }
        catch (AcmeProblemException ex) { logger.LogWarning("ACME cert download error: {Type} — {Detail}", ex.ProblemType, ex.Message); AddAcmeHeaders(ctx, acme); return AcmeProblemResult(ex); }
        catch (Exception ex) { logger.LogError(ex, "ACME cert download internal error"); AddAcmeHeaders(ctx, acme); return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", ex.Message, 500)); }
    });

    // revokeCert (stub — accept but log only)
    app.MapPost("/acme/revoke-cert", async (HttpContext ctx, Rfc8555AcmeService acme, ILogger<Program> logger, CancellationToken ct) =>
    {
        try
        {
            var body = await ReadBodyAsync(ctx.Request);
            var expectedUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/revoke-cert";
            var jws = acme.VerifyJws(body, expectedUrl);
            logger.LogWarning("RFC 8555 revokeCert requested — not implemented, returning success for compatibility");
            AddAcmeHeaders(ctx, acme);
            return Results.Ok();
        }
        catch (AcmeProblemException ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(ex); }
        catch (Exception ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", ex.Message, 500)); }
    });

    // keyChange (stub — not supported)
    app.MapPost("/acme/key-change", async (HttpContext ctx, Rfc8555AcmeService acme, CancellationToken ct) =>
    {
        AddAcmeHeaders(ctx, acme);
        return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", "Key change is not supported by this ACME server.", 501));
    });

    // Helper: format an order for JSON response.
    object FormatOrder(Rfc8555Order order, string baseUrl) => new
    {
        status = order.Status,
        expires = order.Expires.ToString("o"),
        identifiers = order.Identifiers.Select(i => new { type = i.Type, value = i.Value }),
        authorizations = order.Authorizations.Select(a => $"{baseUrl}/acme/authz/{a.Id}"),
        finalize = $"{baseUrl}/acme/order/{order.Id}/finalize",
        certificate = order.CertificateId is not null ? $"{baseUrl}/acme/cert/{order.CertificateId}" : null
    };

    async Task<byte[]> ReadBodyAsync(HttpRequest req)
    {
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms);
        return ms.ToArray();
    }
}

// ── Authenticated endpoints ─────────────────────────────────────────────────

var api = app.MapGroup("/api/v1").AddEndpointFilter<BearerTokenFilter>();

// ── Setup wizard endpoints (authenticated) ──────────────────────────────────

api.MapGet("/setup/state", (SetupStateService setupState) => Results.Ok(new
{
    phase = setupState.Current.SetupPhase.ToString().ToLowerInvariant(),
    isComplete = setupState.IsSetupComplete,
    hostname = setupState.Current.Hostname,
    tlsCertificateId = setupState.Current.TlsCertificateId
}));

api.MapPost("/setup/skip", async (SetupStateService setupState, CancellationToken ct) =>
{
    var state = await setupState.SkipWizardAsync(ct);
    return Results.Ok(new { phase = state.SetupPhase.ToString().ToLowerInvariant(), isComplete = true });
});

api.MapPost("/setup/configure", async (ConfigureInstanceRequest request, SetupStateService setupState, IOptions<HomeCaStorageOptions> storageOptions, ILogger<Program> logger, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Hostname))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["hostname"] = ["Hostname is required."] });

    var publicUrl = $"http://{request.Hostname}:{request.Port ?? 5080}";

    // Persist PublicUrl so it survives restarts — write to /etc/homeca/
    var configPath = "/etc/homeca/public-url.conf";
    try
    {
        await File.WriteAllTextAsync(configPath, publicUrl, ct);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not write PublicUrl to {Path} — will use in-memory value only", configPath);
    }

    // Also update the in-memory options for the current process
    storageOptions.Value.PublicUrl = publicUrl;

    // Store hostname in setup state
    await setupState.SetHostnameAsync(request.Hostname, ct);
    logger.LogInformation("Instance configured: hostname={Hostname}, publicUrl={PublicUrl}", request.Hostname, publicUrl);

    return Results.Ok(new { hostname = request.Hostname, publicUrl });
});

api.MapPost("/setup/activate-tls", async (ActivateTlsRequest request, SetupStateService setupState, CertificateIssuanceService issuance, HomeCaStorage storage, ILogger<Program> logger, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Hostname))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["hostname"] = ["Hostname is required."] });

    try
    {
        // Collect SANs: hostname + optional IPs
        var dnsNames = new List<string> { request.Hostname };
        var ipAddresses = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.IpAddress))
            ipAddresses.Add(request.IpAddress);

        // Issue TLS certificate for this HomeCA instance
        var issueRequest = new IssueCertificateRequest("TLS", dnsNames, ipAddresses, 365, "ECC");
        var result = await issuance.IssueAsync(issueRequest, ct);

        // Store hostname and certificate ID in setup state
        await setupState.SetHostnameAsync(request.Hostname, ct);
        await setupState.SetTlsCertificateIdAsync(result.Id, ct);

        // Write TLS configuration file (readable by the restart helper)
        var pfxPath = Path.Combine(storage.RootPath, "certificates", result.Id, "certificate.pfx");
        var httpsUrl = "https://0.0.0.0:5443";
        var tlsConfig = new
        {
            httpsUrl,
            pfxPath,
            publicUrl = $"https://{request.Hostname}:5443",
            hostname = request.Hostname
        };
        var tlsConfigPath = Path.Combine(Path.GetDirectoryName(storage.RootPath)!, "..", "etc", "homeca", "tls.json");
        // Normalize: /etc/homeca/tls.json
        tlsConfigPath = "/etc/homeca/tls.json";
        await File.WriteAllTextAsync(tlsConfigPath, System.Text.Json.JsonSerializer.Serialize(tlsConfig, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), ct);

        // Advance setup state
        await setupState.AdvanceAsync(SetupPhase.CaInitialized, ct);

        logger.LogInformation("TLS activated for {Hostname}, certificate {CertificateId}. Restart required.", request.Hostname, result.Id);

        return Results.Ok(new
        {
            certificateId = result.Id,
            hostname = request.Hostname,
            httpsUrl,
            message = "TLS configured. Restart the HomeCA service to apply: systemctl restart homeca"
        });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
        return Results.Conflict(new { detail = ex.Message });
    }
});

api.MapPost("/setup/complete", async (SetupStateService setupState, CancellationToken ct) =>
{
    await setupState.AdvanceAsync(SetupPhase.TlsConfigured, ct);
    return Results.Ok(new { phase = "complete", isComplete = true });
});

api.MapPost("/system/activate-tls", async (SetupStateService setupState, HomeCaStorage storage, IHostApplicationLifetime lifetime, ILogger<Program> logger, CancellationToken ct) =>
{
    // Verify that TLS configuration has been generated
    const string tlsConfigPath = "/etc/homeca/tls.json";
    if (!File.Exists(tlsConfigPath))
        return Results.Conflict(new { detail = "TLS-Konfiguration nicht gefunden. Bitte zuerst ein TLS-Zertifikat ausstellen." });

    string tlsJson;
    try
    {
        tlsJson = await File.ReadAllTextAsync(tlsConfigPath, ct);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to read TLS configuration from {Path}", tlsConfigPath);
        return Results.Problem("TLS-Konfigurationsdatei konnte nicht gelesen werden.");
    }

    var tlsConfig = System.Text.Json.JsonSerializer.Deserialize<TlsConfigDto>(tlsJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    if (tlsConfig is null || string.IsNullOrWhiteSpace(tlsConfig.HttpsUrl) || string.IsNullOrWhiteSpace(tlsConfig.PfxPath))
        return Results.Conflict(new { detail = "TLS-Konfiguration ist ungültig." });

    if (!File.Exists(tlsConfig.PfxPath))
        return Results.Conflict(new { detail = $"Zertifikatsdatei nicht gefunden: {tlsConfig.PfxPath}" });

    // Write the systemd override that switches Kestrel to HTTPS.
    // The service runs under ProtectSystem=strict, so /etc/systemd/system
    // is always read-only — we must use sudo for all writes there.
    const string overrideDir = "/etc/systemd/system/homeca.service.d";
    const string overridePath = "/etc/systemd/system/homeca.service.d/tls.conf";

    var mkdirResult = RunProcess("sudo", $"mkdir -p {overrideDir}");
    if (!mkdirResult.Success)
        return Results.Problem($"Systemd-Override-Verzeichnis konnte nicht erstellt werden: {mkdirResult.Error}");

    var overrideLines = new[]
    {
        "[Service]",
        $"Environment=ASPNETCORE_URLS={tlsConfig.HttpsUrl}",
        $"Environment=ASPNETCORE_Kestrel__Certificates__Default__Path={tlsConfig.PfxPath}",
        $"Environment=Storage__PublicUrl={tlsConfig.PublicUrl}",
        ""
    };
    var overrideContent = string.Join('\n', overrideLines);
    var teeResult = RunProcess("sudo", $"tee {overridePath}", overrideContent);
    if (!teeResult.Success)
        return Results.Problem($"Systemd-Override konnte nicht geschrieben werden: {teeResult.Error}");

    logger.LogInformation("Wrote systemd TLS override to {Path} via sudo", overridePath);

    // Reload systemd to pick up the override, then restart the service
    var reload = RunProcess("sudo", "systemctl daemon-reload");
    if (!reload.Success)
    {
        logger.LogError("systemctl daemon-reload failed: {Error}", reload.Error);
        return Results.Problem($"systemctl daemon-reload fehlgeschlagen: {reload.Error}");
    }

    logger.LogInformation("TLS activation complete — restarting service via systemctl");

    // Fire-and-forget: restart the service. The response must be sent before we die.
    _ = Task.Run(async () =>
    {
        await Task.Delay(500); // give the HTTP response time to flush
        RunProcess("sudo", "systemctl restart homeca");
    });

    return Results.Ok(new
    {
        message = "TLS wird aktiviert. HomeCA startet neu und ist gleich unter HTTPS erreichbar.",
        publicUrl = tlsConfig.PublicUrl
    });
});

api.MapPost("/setup/reset", async (SetupStateService setupState, CancellationToken ct) =>
{
    var state = await setupState.ResetAsync(ct);
    return Results.Ok(new { phase = state.SetupPhase.ToString().ToLowerInvariant(), isComplete = false });
});

api.MapPost("/change-password", async (ChangePasswordRequest request, HttpContext context, LocalAdministrationService administration, SetupStateService setupState, CancellationToken ct) =>
{
    var token = context.Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);
    if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 12)
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["password"] = ["Das neue Passwort muss mindestens 12 Zeichen lang sein."] });
    if (!await administration.ChangePasswordAsync(token, request, ct)) return Results.Unauthorized();
    await setupState.AdvanceAsync(SetupPhase.Initial, ct);
    return Results.NoContent();
});

// Authorities
api.MapPost("/authorities/initialize", async (CertificateAuthorityService authorities, SetupStateService setupState, CancellationToken ct) =>
{
    var result = await authorities.InitializeAsync(ct);
    // CA eingerichtet → Wizard ist fertig (TLS ist optional, kann über Einstellungen nachgeholt werden)
    await setupState.AdvanceAsync(SetupPhase.PasswordChanged, ct);
    await setupState.SkipWizardAsync(ct);
    return Results.Ok(result);
});
api.MapGet("/authorities", async (CertificateAuthorityService authorities, CancellationToken ct) => Results.Ok(await authorities.ListAsync(ct)));
api.MapPost("/authorities", async (CreateAuthorityRequest request, CertificateAuthorityService authorities, CancellationToken ct) =>
{
    try { return Results.Ok(await authorities.CreateAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["authority"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapPut("/authorities/{id}", async (string id, UpdateAuthorityRequest request, CertificateAuthorityService authorities, CancellationToken ct) =>
{
    try { var authority = await authorities.UpdateAsync(id, request, ct); return authority is null ? Results.NotFound() : Results.Ok(authority); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapPost("/authorities/{id}/revoke", async (string id, CertificateAuthorityService authorities, CancellationToken ct) =>
{
    try { var authority = await authorities.RevokeAsync(id, ct); return authority is null ? Results.NotFound() : Results.Ok(authority); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapDelete("/authorities/{id}", async (string id, CertificateAuthorityService authorities, CancellationToken ct) =>
{
    try { return await authorities.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapGet("/authorities/{id}/certificate", async (string id, string format, CertificateAuthorityService authorities, CancellationToken ct) =>
{
    try { var export = await authorities.ExportCertificateAsync(id, format, ct); return export is null ? Results.NotFound() : Results.File(export.Content, export.ContentType, export.FileName); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["format"] = [ex.Message] }); }
});

// Certificates
api.MapGet("/certificates", async (HttpRequest httpRequest, CertificateIssuanceService certificates, CancellationToken ct) =>
{
    var search = httpRequest.Query["search"].FirstOrDefault();
    var skip = int.TryParse(httpRequest.Query["skip"], out var s) ? s : 0;
    var take = int.TryParse(httpRequest.Query["take"], out var t) ? t : 100;
    return Results.Ok(await certificates.ListAsync(ct, search, skip, take));
});
api.MapGet("/certificates/{id}", async (string id, CertificateIssuanceService certificates, CancellationToken ct) =>
{
    var details = await certificates.GetDetailsAsync(id, ct);
    return details is null ? Results.NotFound() : Results.Ok(details);
});
api.MapPost("/certificates", async (IssueCertificateRequest request, CertificateIssuanceService certificates, ILogger<Program> logger, CancellationToken ct) =>
{
    try { return Results.Ok(await certificates.IssueAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["certificate"] = [ex.Message] }); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "Certificate issuance failed");
        throw;
    }
});
api.MapDelete("/certificates/{id}", async (string id, HttpRequest httpRequest, CertificateIssuanceService certificates, CancellationToken ct) =>
{
    var reason = httpRequest.Query["reason"].FirstOrDefault() ?? "unspecified";
    return await certificates.RevokeAndDeleteAsync(id, reason, ct) ? Results.NoContent() : Results.NotFound();
});
api.MapGet("/certificates/{id}/export/pem", async (string id, HomeCaStorage storage) =>
{
    var path = Path.Combine(storage.RootPath, "exports", id, "certificate.pem");
    if (!File.Exists(path)) return Results.NotFound();
    var name = CertificateDownloadName(storage, id);
    return Results.File(path, "application/x-pem-file", $"{name}.pem");
});
api.MapGet("/certificates/{id}/export/chain", async (string id, HomeCaStorage storage) =>
{
    var path = Path.Combine(storage.RootPath, "exports", id, "chain.pem");
    if (!File.Exists(path)) return Results.NotFound();
    var name = CertificateDownloadName(storage, id);
    return Results.File(path, "application/x-pem-file", $"{name}-chain.pem");
});
api.MapGet("/certificates/{id}/export/key", async (string id, HomeCaStorage storage) =>
{
    var path = Path.Combine(storage.RootPath, "exports", id, "key.pem");
    if (!File.Exists(path)) return Results.NotFound();
    var name = CertificateDownloadName(storage, id);
    return Results.File(path, "application/x-pem-file", $"{name}-key.pem");
});
api.MapGet("/certificates/{id}/export/fullchain", async (string id, HomeCaStorage storage) =>
{
    var path = Path.Combine(storage.RootPath, "exports", id, "fullchain.pem");
    if (!File.Exists(path)) return Results.NotFound();
    var name = CertificateDownloadName(storage, id);
    return Results.File(path, "application/x-pem-file", $"{name}-fullchain.pem");
});
api.MapGet("/certificates/{id}/export/bundle", async (string id, HomeCaStorage storage) =>
{
    var path = Path.Combine(storage.RootPath, "exports", id, "bundle.pem");
    if (!File.Exists(path)) return Results.NotFound();
    var name = CertificateDownloadName(storage, id);
    return Results.File(path, "application/x-pem-file", $"{name}-bundle.pem");
});
api.MapPost("/certificates/{id}/export/pfx", async (string id, PfxExportRequest request, HomeCaStorage storage) =>
{
    var pfxPath = Path.Combine(storage.RootPath, "certificates", id, "certificate.pfx");
    if (!File.Exists(pfxPath)) return Results.NotFound();
    using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
    var bytes = certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pkcs12, request.Password);
    var name = CertificateDownloadName(storage, id);
    return Results.File(bytes, "application/x-pkcs12", $"{name}.pfx");
});

// SSH
api.MapGet("/ssh-certificates", async (SshCertificateService certificates, CancellationToken ct) => Results.Ok(await certificates.ListAsync(ct)));
api.MapGet("/ssh-certificates/{id}/content", async (string id, SshCertificateService certificates, CancellationToken ct) =>
{
    var content = await certificates.GetCertificateContentAsync(id, ct);
    return content is null ? Results.NotFound() : Results.Text(content, "text/plain");
});
api.MapDelete("/ssh-certificates/{id}", async (string id, SshCertificateService certificates, CancellationToken ct) =>
    await certificates.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
api.MapPost("/ssh-certificates", async (SshIssueRequest request, SshCertificateService certificates, ILogger<Program> logger, CancellationToken ct) =>
{
    try { return Results.Ok(await certificates.IssueAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["ssh"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "SSH certificate issuance failed");
        throw;
    }
});
api.MapGet("/ssh-ca-keys/{kind}", async (string kind, SshCertificateService certificates, CancellationToken ct) =>
{
    var key = await certificates.GetCaPublicKeyAsync(kind, ct);
    return key is null ? Results.NotFound(new { detail = "SSH CA key not found. Initialize certificate authorities first." }) : Results.Ok(key);
});

// Domains
api.MapGet("/domains", async (DomainRegistry domains, CancellationToken ct) => Results.Ok(await domains.ListAsync(ct)));
api.MapPost("/domains", async (CreateDomainRequest request, DomainRegistry domains, CancellationToken ct) => Results.Ok(await domains.AddAsync(request, ct)));
api.MapPut("/domains/{name}", async (string name, CreateDomainRequest request, DomainRegistry domains, CancellationToken ct) =>
{
    try { var domain = await domains.UpdateAsync(name, request, ct); return domain is null ? Results.NotFound() : Results.Ok(domain); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapDelete("/domains/{name}", async (string name, DomainRegistry domains, CancellationToken ct) => await domains.DeleteAsync(name, ct) ? Results.NoContent() : Results.NotFound());

// Profiles
api.MapGet("/profiles", async (TargetProfileRegistry profiles, CancellationToken ct) => Results.Ok(await profiles.ListAsync(ct)));
api.MapPost("/profiles", async (CreateTargetProfileRequest request, TargetProfileRegistry profiles, CancellationToken ct) =>
{
    try { return Results.Ok(await profiles.AddAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["profile"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapPut("/profiles/{id}", async (string id, UpdateTargetProfileRequest request, TargetProfileRegistry profiles, CancellationToken ct) =>
{
    try { var profile = await profiles.UpdateAsync(id, request, ct); return profile is null ? Results.NotFound() : Results.Ok(profile); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["profile"] = [ex.Message] }); }
});
api.MapDelete("/profiles/{id}", async (string id, TargetProfileRegistry profiles, CancellationToken ct) =>
{
    try { return await profiles.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound(); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});

// Connectors
api.MapGet("/connector-instances", async (ConnectorRegistry connectors, CancellationToken ct) => Results.Ok((await connectors.ListAsync(ct)).Select(c => new { c.Id, c.Name, c.Type, c.CreatedAt })));
api.MapPost("/connector-instances", async (CreateConnectorRequest request, ConnectorRegistry connectors, CancellationToken ct) =>
{
    try { return Results.Ok(await connectors.AddAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["connector"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapPut("/connector-instances/{id}", async (string id, CreateConnectorRequest request, ConnectorRegistry connectors, CancellationToken ct) =>
{
    try { var connector = await connectors.UpdateAsync(id, request, ct); return connector is null ? Results.NotFound() : Results.Ok(connector); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["connector"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapPost("/connector-instances/{id}/check", async (string id, ConnectorRegistry registry, ConnectorCatalog catalog, ILogger<Program> logger, CancellationToken ct) =>
{
    var connector = await registry.GetAsync(id, ct);
    var implementation = connector is null ? null : catalog.Find(connector.Type);
    if (connector is null || implementation is null) return Results.NotFound();
    try { return Results.Ok(await implementation.CheckAsync(new ConnectorSettings(connector.Name, connector.Type, connector.Secrets), ct)); }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Connector check failed for {ConnectorId} ({Type})", id, connector.Type);
        return Results.Ok(new ConnectorCheckResult(false, [], "The connector could not be reached. Check its settings and network access."));
    }
});
api.MapPost("/connector-instances/{id}/txt-test", async (string id, HttpRequest request, ConnectorRegistry registry, ConnectorCatalog catalog, CancellationToken ct) =>
{
    var connector = await registry.GetAsync(id, ct);
    var implementation = connector is null ? null : catalog.Find(connector.Type);
    if (connector is null || implementation is null) return Results.NotFound();
    var domain = request.Query["domain"].ToString();
    if (string.IsNullOrWhiteSpace(domain)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["domain"] = ["A domain query parameter is required."] });
    var name = $"_homeca-test.{domain}".TrimEnd('.');
    var value = Guid.NewGuid().ToString("N");
    var settings = new ConnectorSettings(connector.Name, connector.Type, connector.Secrets);
    await implementation.UpsertTxtRecordAsync(settings, name, value, ct);
    await implementation.DeleteTxtRecordAsync(settings, name, value, ct);
    return Results.NoContent();
});
api.MapDelete("/connector-instances/{id}", async (string id, ConnectorRegistry connectors, CancellationToken ct) => await connectors.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

// ACME (authenticated)
api.MapGet("/acme/accounts", async (InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.ListAccountsAsync(ct)));
api.MapGet("/acme/orders", async (InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.ListOrdersAsync(ct)));
api.MapPost("/acme/orders", async (AcmeOrderRequest request, InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.CreateOrderAsync(request.AccountId, request.Identifiers, ct)));
api.MapGet("/acme/orders/{orderId}", async (string orderId, InternalAcmeService acme, CancellationToken ct) =>
{
    var order = await acme.GetOrderAsync(orderId, ct);
    return order is null ? Results.NotFound() : Results.Ok(order);
});
api.MapPost("/acme/orders/{orderId}/finalize", async (string orderId, FinalizeAcmeOrderRequest request, InternalAcmeService acme, CancellationToken ct) => Results.Ok(await acme.FinalizeOrderAsync(orderId, request, ct)));
api.MapGet("/acme/external-issuers", async (ExternalAcmeIssuerRegistry issuers, CancellationToken ct) => Results.Ok(await issuers.ListAsync(ct)));
api.MapPost("/acme/external-issuers", async (CreateExternalAcmeIssuerRequest request, ExternalAcmeIssuerRegistry issuers, CancellationToken ct) => Results.Ok(await issuers.AddAsync(request, ct)));
api.MapPut("/acme/external-issuers/{id}", async (string id, CreateExternalAcmeIssuerRequest request, ExternalAcmeIssuerRegistry issuers, CancellationToken ct) =>
{
    var updated = await issuers.UpdateAsync(id, request, ct);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});
api.MapDelete("/acme/external-issuers/{id}", async (string id, ExternalAcmeIssuerRegistry issuers, CancellationToken ct) => await issuers.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
api.MapPost("/acme/external-orders", async (ExternalAcmeOrderRequest request, ExternalAcmeService externalAcme, CancellationToken ct) =>
{
    try { return Results.Ok(await externalAcme.RequestCertificateAsync(request, ct)); }
    catch (ArgumentException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["order"] = [ex.Message] }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
});
api.MapGet("/acme/external-certificates", async (ExternalAcmeService externalAcme, CancellationToken ct) => Results.Ok(await externalAcme.ListCertificatesAsync(ct)));
api.MapDelete("/acme/accounts/{id}", async (string id, InternalAcmeService acme, CancellationToken ct) => await acme.DeleteAccountAsync(id, ct) ? Results.NoContent() : Results.NotFound());
api.MapDelete("/acme/orders/{orderId}", async (string orderId, InternalAcmeService acme, CancellationToken ct) => await acme.DeleteOrderAsync(orderId, ct) ? Results.NoContent() : Results.NotFound());

// RFC 8555 ACME management (authenticated)
api.MapGet("/acme/rfc8555-accounts", async (Rfc8555AcmeService acme, CancellationToken ct) => Results.Ok(await acme.ListAccountsAsync(ct)));
api.MapDelete("/acme/rfc8555-accounts/{id}", async (string id, Rfc8555AcmeService acme, CancellationToken ct) => await acme.DeleteAccountAsync(id, ct) ? Results.NoContent() : Results.NotFound());
api.MapGet("/acme/rfc8555-orders", async (Rfc8555AcmeService acme, CancellationToken ct) => Results.Ok(await acme.ListOrdersAsync(ct)));
api.MapDelete("/acme/rfc8555-orders/{id}", async (string id, Rfc8555AcmeService acme, CancellationToken ct) => await acme.DeleteOrderAsync(id, ct) ? Results.NoContent() : Results.NotFound());

// Renewal plans
api.MapGet("/renewal-plans", async (RenewalPlanRegistry plans, CancellationToken ct) => Results.Ok(await plans.ListAsync(ct)));
api.MapPost("/renewal-plans", async (CreateRenewalPlanRequest body, RenewalPlanRegistry plans, CancellationToken ct) => Results.Ok(await plans.AddAsync(body, ct)));
api.MapPut("/renewal-plans/{id}", async (string id, UpdateRenewalPlanRequest body, RenewalPlanRegistry plans, CancellationToken ct) =>
{
    var plan = await plans.UpdateAsync(id, body, ct);
    return plan is null ? Results.NotFound() : Results.Ok(plan);
});
api.MapDelete("/renewal-plans/{id}", async (string id, RenewalPlanRegistry plans, CancellationToken ct) => await plans.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

// Operations
api.MapGet("/warnings/expiring", (CertificateExpiryService expiry) => Results.Ok(expiry.GetWarnings()));
api.MapGet("/revocations", async (RevocationRegistry registry, CancellationToken ct) => Results.Ok(await registry.ListAsync(ct)));
api.MapPost("/revocations/{serial}/{reason}", async (string serial, string reason, RevocationRegistry registry, CancellationToken ct) => Results.Ok(await registry.RevokeAsync(serial, reason, ct)));
api.MapPost("/crl", async (CrlService crl, ILogger<Program> logger, CancellationToken ct) =>
{
    try { return Results.Ok(new { path = await crl.GenerateAsync(ct) }); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { detail = ex.Message }); }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "CRL generation failed");
        throw;
    }
});

// Backups
api.MapPost("/backups", async (HomeCaStorage storage, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        var backup = await storage.CreateBackupAsync(ct);
        return Results.Created($"/api/v1/backups/{backup.FileName}", backup);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogError(ex, "Backup creation failed");
        throw;
    }
});
api.MapPost("/backups/{fileName}/verify", async (string fileName, HomeCaStorage storage, CancellationToken ct) => Results.Ok(await storage.VerifyBackupAsync(fileName, ct)));
api.MapGet("/backups/{fileName}", (string fileName, HomeCaStorage storage) =>
{
    var safeName = Path.GetFileName(fileName);
    if (!string.Equals(safeName, fileName, StringComparison.Ordinal) || !safeName.EndsWith(".hcab", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { detail = "Invalid backup file name." });
    var path = storage.ResolveBackupPath(safeName);
    return !File.Exists(path) ? Results.NotFound(new { detail = "Backup not found." }) : Results.File(path, "application/octet-stream", safeName);
});
api.MapPut("/backups", async (HttpRequest request, HomeCaStorage storage, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { detail = "Multipart form data required." });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest(new { detail = "No file uploaded." });
    var safeName = Path.GetFileName(file.FileName);
    if (!safeName.EndsWith(".hcab", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { detail = "Only .hcab backup files are accepted." });
    var targetPath = storage.ResolveBackupPath(safeName);
    if (File.Exists(targetPath)) return Results.Conflict(new { detail = $"Backup '{safeName}' already exists." });
    await using var stream = File.Create(targetPath);
    await file.CopyToAsync(stream, ct);
    logger.LogInformation("Uploaded backup {BackupFile}", safeName);
    return Results.Created($"/api/v1/backups/{safeName}", new { fileName = safeName });
});
api.MapGet("/backups", (HomeCaStorage storage) =>
{
    var dir = storage.ResolveBackupPath(".");
    if (!Directory.Exists(dir)) return Results.Ok(Array.Empty<object>());
    var files = Directory.GetFiles(dir, "*.hcab")
        .Select(f => new FileInfo(f))
        .OrderByDescending(f => f.CreationTimeUtc)
        .Select(f => new { fileName = f.Name, size = f.Length, createdAt = f.CreationTimeUtc })
        .ToArray();
    return Results.Ok(files);
});
api.MapDelete("/backups/{fileName}", (string fileName, HomeCaStorage storage, ILogger<Program> logger) =>
{
    var safeName = Path.GetFileName(fileName);
    if (!string.Equals(safeName, fileName, StringComparison.Ordinal) || !safeName.EndsWith(".hcab", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { detail = "Invalid backup file name." });
    var path = storage.ResolveBackupPath(safeName);
    if (!File.Exists(path)) return Results.NotFound(new { detail = "Backup not found." });
    File.Delete(path);
    logger.LogInformation("Deleted backup {BackupFile}", safeName);
    return Results.NoContent();
});

// Backup key
api.MapGet("/backup-key", async (HomeCaStorage storage, CancellationToken ct) =>
{
    var keyPath = storage.BackupKeyPath;
    if (!File.Exists(keyPath)) return Results.NotFound(new { detail = "Backup key not found." });
    var bytes = await File.ReadAllBytesAsync(keyPath, ct);
    return Results.File(bytes, "application/octet-stream", "backup.key");
});
api.MapPut("/backup-key", async (HttpRequest request, HomeCaStorage storage, ILogger<Program> logger, CancellationToken ct) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { detail = "Multipart form data required." });
    var form = await request.ReadFormAsync(ct);
    var file = form.Files.GetFile("file");
    if (file is null || file.Length == 0) return Results.BadRequest(new { detail = "No file uploaded." });
    if (file.Length != 32) return Results.BadRequest(new { detail = "The backup key must be exactly 32 bytes (AES-256)." });
    var keyPath = storage.BackupKeyPath;
    await using var stream = File.Create(keyPath);
    await file.CopyToAsync(stream, ct);
    logger.LogInformation("Backup key uploaded to {Path}", keyPath);
    return Results.NoContent();
});

// Audit
api.MapGet("/audit", async (HttpRequest request, LocalAdministrationService administration, CancellationToken ct) =>
{
    var skip = int.TryParse(request.Query["skip"], out var s) ? s : 0;
    var take = int.TryParse(request.Query["take"], out var t) ? t : 100;
    var action = request.Query["action"].FirstOrDefault();
    return Results.Ok(await administration.ReadAuditLogAsync(skip, take, action, ct));
});

// ── Blazor UI ───────────────────────────────────────────────────────────────

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

await app.RunAsync();

/// <summary>Extracts a filesystem-safe download name from the certificate's CN, falling back to the hex ID.</summary>
static string CertificateDownloadName(HomeCaStorage storage, string id)
{
    try
    {
        var pfxPath = Path.Combine(storage.RootPath, "certificates", id, "certificate.pfx");
        if (!File.Exists(pfxPath)) return id;
        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
        var cn = certificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
        if (string.IsNullOrWhiteSpace(cn)) return id;
        // Replace characters that are unsafe in filenames
        var sanitized = string.Concat(cn.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(sanitized) ? id : sanitized;
    }
    catch
    {
        return id;
    }
}

/// <summary>Runs an external process with optional stdin and returns success + output.</summary>
static ProcessResult RunProcess(string fileName, string arguments, string? stdin = null)
{
    using var process = new System.Diagnostics.Process();
    process.StartInfo = new System.Diagnostics.ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        RedirectStandardInput = stdin is not null,
        UseShellExecute = false,
        CreateNoWindow = true,
        // Do not inherit the service's configured working directory. An older
        // installation can still point at /opt/homeca after the application
        // was moved, which prevents even `sudo` from being started.
        WorkingDirectory = "/"
    };
    process.Start();
    if (stdin is not null)
    {
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
    }
    var output = process.StandardOutput.ReadToEnd();
    var error = process.StandardError.ReadToEnd();
    process.WaitForExit(TimeSpan.FromSeconds(30));
    return new ProcessResult(process.ExitCode == 0, output.Trim(), error.Trim());
}

record ProcessResult(bool Success, string Output, string Error);
record TlsConfigDto(string? HttpsUrl, string? PfxPath, string? PublicUrl, string? Hostname);
