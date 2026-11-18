using System.Threading.RateLimiting;

namespace HomeCA.Service.Security;

/// <summary>
/// Authenticates the <c>/api/v1/issuance</c> group with a scoped issuance token (<c>Bearer hca_it_…</c>).
/// Administrator sessions are not accepted here, and <see cref="BearerTokenFilter"/> never accepts these tokens.
/// </summary>
public sealed class IssuanceTokenFilter(IssuanceTokenRegistry tokens, LocalAdministrationService administration, IConfiguration configuration, ILogger<IssuanceTokenFilter> logger) : IEndpointFilter
{
    public const string TokenItemKey = "HomeCA.IssuanceToken";

    // A separate instance: failed token guesses must not lock the administrator out of the login.
    private readonly LoginRateLimiter _invalidTokens = new();

    private readonly PartitionedRateLimiter<string> _issuance = PartitionedRateLimiter.Create<string, string>(tokenId =>
        RateLimitPartition.GetFixedWindowLimiter(tokenId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, configuration.GetValue("IssuanceTokens:RequestsPerHour", 30)),
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0
        }));

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var address = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (_invalidTokens.IsBlocked(address)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);

        var header = http.Request.Headers.Authorization.ToString();
        var secret = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..].Trim() : null;
        var token = await tokens.ResolveAsync(secret, http.RequestAborted);
        var now = DateTimeOffset.UtcNow;
        if (token is null)
        {
            _invalidTokens.RecordFailure(address);
            logger.LogWarning("Rejected an unknown issuance token from {RemoteIpAddress}", address);
            return Results.Unauthorized();
        }
        if (!token.IsUsable(now))
        {
            _invalidTokens.RecordFailure(address);
            var reason = token.RevokedAt is not null ? "revoked" : "expired";
            await administration.RecordAuditAsync("issuance-token.denied", $"{Describe(token)}: token {reason}, {http.Request.Method} {http.Request.Path} from {address}", http.RequestAborted);
            return Results.Unauthorized();
        }

        _invalidTokens.RecordSuccess(address);
        http.Items[TokenItemKey] = token;
        await tokens.TouchAsync(token.Id, now, http.RequestAborted);
        return await next(context);
    }

    /// <summary>Takes one issuance permit for the token; false when the hourly limit is used up.</summary>
    public bool TryAcquireIssuance(IssuanceToken token)
    {
        using var lease = _issuance.AttemptAcquire(token.Id);
        return lease.IsAcquired;
    }

    internal static string Describe(IssuanceToken token) => $"{token.Name} ({token.Id})";
}
