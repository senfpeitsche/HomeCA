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
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HomeCA.Service.Endpoints;
 static class Rfc8555AcmeEndpoints
{
    public static void MapRfc8555AcmeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        {
            string AcmeBaseUrl(HttpRequest req)
            {
                var storage = req.HttpContext.RequestServices.GetRequiredService<HomeCaStorage>();
                if (!Uri.TryCreate(storage.PublicUrl, UriKind.Absolute, out var publicUrl)
                    || publicUrl.Scheme is not ("http" or "https"))
                    throw new InvalidOperationException("Storage:PublicUrl must be configured as an absolute HTTP(S) URL before using ACME.");
                return publicUrl.AbsoluteUri.TrimEnd('/');
            }
        
            IResult AcmeProblemResult(AcmeProblemException ex) => Results.Json(
                new { type = $"urn:ietf:params:acme:error:{ex.ProblemType}", detail = ex.Message, status = ex.StatusCode },
                statusCode: ex.StatusCode, contentType: "application/problem+json");
        
            // Directory
            endpoints.MapGet("/acme/directory", (HttpRequest req, Rfc8555AcmeService acme) =>
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
            endpoints.MapMethods("/acme/new-nonce", ["HEAD", "GET"], (HttpContext ctx, Rfc8555AcmeService acme) =>
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
            endpoints.MapPost("/acme/new-acct", async (HttpContext ctx, Rfc8555AcmeService acme, AcmeAccessPolicyRegistry accessPolicy, ILogger<global::Program> logger, CancellationToken ct) =>
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
        
                    var existing = await acme.FindAccountByThumbprintAsync(jws.Thumbprint!, ct);
                    if (onlyReturnExisting)
                    {
                        if (existing is null)
                            return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("accountDoesNotExist", "No account found for this key."));
        
                        var existingUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/acct/{existing.Id}";
                        AddAcmeHeaders(ctx, acme, existingUrl);
                        return Results.Json(new { status = existing.Status, contact = existing.Contact, orders = $"{AcmeBaseUrl(ctx.Request)}/acme/acct/{existing.Id}/orders" }, statusCode: 200);
                    }
        
                    // Existing accounts have already passed their admission check. A new
                    // account is admitted either from the configured client-network
                    // allowlist or through RFC 8555 External Account Binding (EAB).
                    string? eabKeyId = null;
                    if (existing is null)
                    {
                        var binding = System.Text.Json.Nodes.JsonNode.Parse(jws.Payload)?.AsObject()?["externalAccountBinding"]?.AsObject();
                        eabKeyId = await accessPolicy.AuthorizeNewAccountAsync(ctx.Connection.RemoteIpAddress, binding, jws.Jwk, expectedUrl, ct);
                    }

                    var account = await acme.NewAccountAsync(jws.Jwk, contact, ct);
                    if (eabKeyId is not null) await accessPolicy.AssociateEabWithAccountAsync(eabKeyId, account.Id, ct);
                    var accountUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/acct/{account.Id}";
                    AddAcmeHeaders(ctx, acme, accountUrl);
                    logger.LogInformation("ACME newAccount: registered/found account {AccountId}, url={Url}", account.Id, accountUrl);
                    return Results.Json(new { status = account.Status, contact = account.Contact, orders = $"{AcmeBaseUrl(ctx.Request)}/acme/acct/{account.Id}/orders" }, statusCode: 201);
                }
                catch (AcmeProblemException ex) { logger.LogWarning("ACME newAccount error: {Type} — {Detail}", ex.ProblemType, ex.Message); AddAcmeHeaders(ctx, acme); return AcmeProblemResult(ex); }
                catch (Exception ex) { logger.LogError(ex, "ACME newAccount internal error"); AddAcmeHeaders(ctx, acme); return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", ex.Message, 500)); }
            });
        
            // Account lookup (POST-as-GET)
            endpoints.MapPost("/acme/acct/{accountId}", async (string accountId, HttpContext ctx, Rfc8555AcmeService acme, ILogger<global::Program> logger, CancellationToken ct) =>
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
            endpoints.MapPost("/acme/new-order", async (HttpContext ctx, Rfc8555AcmeService acme, ILogger<global::Program> logger, CancellationToken ct) =>
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
            endpoints.MapPost("/acme/order/{orderId}", async (string orderId, HttpContext ctx, Rfc8555AcmeService acme, CancellationToken ct) =>
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
            endpoints.MapPost("/acme/authz/{authzId}", async (string authzId, HttpContext ctx, Rfc8555AcmeService acme, ILogger<global::Program> logger, CancellationToken ct) =>
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
            endpoints.MapPost("/acme/chall/{challengeId}", async (string challengeId, HttpContext ctx, Rfc8555AcmeService acme, ILogger<global::Program> logger, CancellationToken ct) =>
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
            endpoints.MapPost("/acme/order/{orderId}/finalize", async (string orderId, HttpContext ctx, Rfc8555AcmeService acme, ILogger<global::Program> logger, CancellationToken ct) =>
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
            endpoints.MapPost("/acme/cert/{certificateId}", async (string certificateId, HttpContext ctx, Rfc8555AcmeService acme, ILogger<global::Program> logger, CancellationToken ct) =>
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
        
            // revokeCert
            endpoints.MapPost("/acme/revoke-cert", async (HttpContext ctx, Rfc8555AcmeService acme, RevocationRegistry revocations, CrlService crl, CertificateAuthorityService authorities, ILogger<global::Program> logger, CancellationToken ct) =>
            {
                try
                {
                    var body = await ReadBodyAsync(ctx.Request);
                    var expectedUrl = $"{AcmeBaseUrl(ctx.Request)}/acme/revoke-cert";
                    var jws = acme.VerifyJws(body, expectedUrl);

                    var payload = System.Text.Json.Nodes.JsonNode.Parse(jws.Payload)?.AsObject()
                        ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "revokeCert payload is required.");
                    var certificateDer = Rfc8555AcmeService.Base64UrlDecode(payload["certificate"]?.GetValue<string>()
                        ?? throw Rfc8555AcmeService.AcmeProblem("malformed", "Missing certificate in revokeCert payload."));
                    using var certificate = X509CertificateLoader.LoadCertificate(certificateDer);
                    if (!acme.IsManagedCertificate(certificate))
                        throw Rfc8555AcmeService.AcmeProblem("malformed", "Certificate was not issued by this ACME server.");

                    var certificateId = certificate.SerialNumber.ToLowerInvariant();
                    if (jws.Kid is not null)
                    {
                        var account = await acme.FindAccountByKidAsync(jws.Kid, ct);
                        if (account is null)
                            throw Rfc8555AcmeService.AcmeProblem("unauthorized", "Account not found.");

                        var storedJwk = System.Text.Json.Nodes.JsonNode.Parse(account.JwkJson)?.AsObject()
                            ?? throw Rfc8555AcmeService.AcmeProblem("serverInternal", "Stored account key is invalid.", 500);
                        var algorithm = jws.ProtectedHeader["alg"]?.GetValue<string>()
                            ?? throw Rfc8555AcmeService.AcmeProblem("badSignatureAlgorithm", "Missing JWS algorithm.");
                        acme.VerifySignatureWithStoredKey(storedJwk, algorithm, body);
                        if (!await acme.IsCertificateOwnedByAccountAsync(certificateId, account.Id, ct))
                            throw Rfc8555AcmeService.AcmeProblem("unauthorized", "Account does not own this certificate.");
                    }
                    else if (jws.Jwk is null || !IsCertificatePublicKey(jws.Jwk, certificate))
                    {
                        throw Rfc8555AcmeService.AcmeProblem("unauthorized", "JWS key does not match the certificate public key.");
                    }

                    var reason = ParseRevocationReason(payload["reason"]);
                    var authorityId = await authorities.FindIssuingIdBySubjectAsync(certificate.Issuer, ct);
                    if (authorityId is null)
                        throw Rfc8555AcmeService.AcmeProblem("malformed", "Certificate issuer is not an active issuing CA.");

                    await revocations.RevokeAsync(certificate.SerialNumber, reason, ct, authorityId);
                    await crl.GenerateAsync(authorityId, ct);
                    logger.LogInformation("RFC 8555 revoked certificate {CertificateId} for reason {Reason}", certificateId, reason);
                    AddAcmeHeaders(ctx, acme);
                    return Results.Ok();
                }
                catch (AcmeProblemException ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(ex); }
                catch (Exception ex) { AddAcmeHeaders(ctx, acme); return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", ex.Message, 500)); }
            });
        
            // keyChange (stub — not supported)
            endpoints.MapPost("/acme/key-change", async (HttpContext ctx, Rfc8555AcmeService acme, CancellationToken ct) =>
            {
                AddAcmeHeaders(ctx, acme);
                return AcmeProblemResult(Rfc8555AcmeService.AcmeProblem("serverInternal", "Key change is not supported by this ACME server.", 501));
            });
        
            // Helper: format an order for JSON response.
            static bool IsCertificatePublicKey(System.Text.Json.Nodes.JsonObject jwk, X509Certificate2 certificate)
            {
                var keyType = jwk["kty"]?.GetValue<string>();
                if (keyType == "RSA")
                {
                    using var rsa = certificate.GetRSAPublicKey();
                    if (rsa is null) return false;
                    var parameters = rsa.ExportParameters(false);
                    return Matches(jwk["n"], parameters.Modulus) && Matches(jwk["e"], parameters.Exponent);
                }

                if (keyType == "EC")
                {
                    using var ecdsa = certificate.GetECDsaPublicKey();
                    if (ecdsa is null) return false;
                    var parameters = ecdsa.ExportParameters(false);
                    var curve = parameters.Curve.Oid.Value switch
                    {
                        "1.2.840.10045.3.1.7" => "P-256",
                        "1.3.132.0.34" => "P-384",
                        "1.3.132.0.35" => "P-521",
                        _ => null
                    };
                    return curve is not null && curve == jwk["crv"]?.GetValue<string>() &&
                           Matches(jwk["x"], parameters.Q.X) && Matches(jwk["y"], parameters.Q.Y);
                }

                return false;

                static bool Matches(System.Text.Json.Nodes.JsonNode? value, byte[]? expected) =>
                    expected is not null && value?.GetValue<string>() is { } encoded &&
                    CryptographicOperations.FixedTimeEquals(Rfc8555AcmeService.Base64UrlDecode(encoded), expected);
            }

            static string ParseRevocationReason(System.Text.Json.Nodes.JsonNode? reason)
            {
                if (reason is null) return "unspecified";
                if (reason is not System.Text.Json.Nodes.JsonValue value || !value.TryGetValue<int>(out var code))
                    throw Rfc8555AcmeService.AcmeProblem("malformed", "Invalid revocation reason.");

                return code switch
                {
                0 => "unspecified",
                1 => "keyCompromise",
                2 => "cACompromise",
                3 => "affiliationChanged",
                4 => "superseded",
                5 => "cessationOfOperation",
                6 => "certificateHold",
                8 => "removeFromCRL",
                9 => "privilegeWithdrawn",
                10 => "aACompromise",
                _ => throw Rfc8555AcmeService.AcmeProblem("malformed", "Invalid revocation reason.")
                };
            }

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
    }

    private sealed record Rfc8555IdentifierDto(string Type, string Value);
    private sealed record Rfc8555ChallengeDetailsDto(string Id, string Type, string Status, DateTimeOffset? ValidatedAt);
    private sealed record Rfc8555AuthorizationDetailsDto(string Id, Rfc8555IdentifierDto Identifier, string Status, DateTimeOffset Expires, IReadOnlyList<Rfc8555ChallengeDetailsDto> Challenges);
    private sealed record Rfc8555OrderDetailsDto(string Id, string AccountId, IReadOnlyList<Rfc8555IdentifierDto> Identifiers, string Status, DateTimeOffset CreatedAt, DateTimeOffset Expires, string? CertificateId, string? Error, IReadOnlyList<Rfc8555AuthorizationDetailsDto> Authorizations);
    private sealed record Rfc8555AccountOrderDto(string Id, string Status, DateTimeOffset CreatedAt);
    private sealed record Rfc8555AccountDetailsDto(string Id, string Thumbprint, string[] Contact, string Status, DateTimeOffset CreatedAt, IReadOnlyList<Rfc8555AccountOrderDto> Orders);
}
