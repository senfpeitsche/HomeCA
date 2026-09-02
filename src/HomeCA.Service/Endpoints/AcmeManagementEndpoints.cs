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

namespace HomeCA.Service.Endpoints;
 static class AcmeManagementEndpoints
{
    public static void MapAcmeManagementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").AddEndpointFilter<BearerTokenFilter>();
        
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
        api.MapGet("/acme/rfc8555-accounts/{id}", async (string id, Rfc8555AcmeService acme, CancellationToken ct) =>
        {
            var account = await acme.GetAccountAsync(id, ct);
            if (account is null) return Results.NotFound();
            var orders = await acme.ListOrdersAsync(ct);
            return Results.Ok(new Rfc8555AccountDetailsDto(
                account.Id, account.Thumbprint, account.Contact, account.Status, account.CreatedAt,
                orders.Where(order => order.AccountId == account.Id)
                    .Select(order => new Rfc8555AccountOrderDto(order.Id, order.Status, order.CreatedAt))
                    .OrderByDescending(order => order.CreatedAt)
                    .ToList()));
        });
        api.MapGet("/acme/rfc8555-orders/{id}", async (string id, Rfc8555AcmeService acme, CancellationToken ct) =>
        {
            var order = await acme.GetOrderAsync(id, ct);
            if (order is null) return Results.NotFound();
            return Results.Ok(new Rfc8555OrderDetailsDto(
                order.Id, order.AccountId,
                order.Identifiers.Select(identifier => new Rfc8555IdentifierDto(identifier.Type, identifier.Value)).ToList(),
                order.Status, order.CreatedAt, order.Expires, order.CertificateId, order.Error,
                order.Authorizations.Select(authorization => new Rfc8555AuthorizationDetailsDto(
                    authorization.Id,
                    new Rfc8555IdentifierDto(authorization.Identifier.Type, authorization.Identifier.Value),
                    authorization.Status,
                    authorization.Expires,
                    authorization.Challenges.Select(challenge => new Rfc8555ChallengeDetailsDto(
                        challenge.Id, challenge.Type, challenge.Status, challenge.ValidatedAt)).ToList())).ToList()));
        });
        
        // RFC 8555 admission: allowlisted client networks may create accounts without
        // EAB; every other client needs the EAB credential returned only on rotation.
        api.MapGet("/acme/access-policy", async (AcmeAccessPolicyRegistry policy, CancellationToken ct) => Results.Ok(await policy.GetAsync(ct)));
        api.MapPut("/acme/access-policy", async (UpdateAcmeAccessPolicyRequest request, AcmeAccessPolicyRegistry policy, CancellationToken ct) =>
        {
            try { return Results.Ok(await policy.UpdateAsync(request, ct)); }
            catch (ArgumentException exception) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["allowlistedClientNetworks"] = [exception.Message] }); }
        });
        api.MapPost("/acme/access-policy/eab/rotate", async (AcmeAccessPolicyRegistry policy, CancellationToken ct) => Results.Ok(await policy.RotateEabAsync(ct)));
        
        // Renewal plans
    }

    private sealed record Rfc8555IdentifierDto(string Type, string Value);
    private sealed record Rfc8555ChallengeDetailsDto(string Id, string Type, string Status, DateTimeOffset? ValidatedAt);
    private sealed record Rfc8555AuthorizationDetailsDto(string Id, Rfc8555IdentifierDto Identifier, string Status, DateTimeOffset Expires, IReadOnlyList<Rfc8555ChallengeDetailsDto> Challenges);
    private sealed record Rfc8555OrderDetailsDto(string Id, string AccountId, IReadOnlyList<Rfc8555IdentifierDto> Identifiers, string Status, DateTimeOffset CreatedAt, DateTimeOffset Expires, string? CertificateId, string? Error, IReadOnlyList<Rfc8555AuthorizationDetailsDto> Authorizations);
    private sealed record Rfc8555AccountOrderDto(string Id, string Status, DateTimeOffset CreatedAt);
    private sealed record Rfc8555AccountDetailsDto(string Id, string Thumbprint, string[] Contact, string Status, DateTimeOffset CreatedAt, IReadOnlyList<Rfc8555AccountOrderDto> Orders);
}
