namespace HomeCA.Service.Security;

/// <summary>
/// Endpoint filter that validates Bearer tokens against <see cref="LocalAdministrationService"/>.
/// Apply to a route group to centralize authentication instead of repeating token checks in every handler.
/// </summary>
public sealed class BearerTokenFilter(LocalAdministrationService administration) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var token = context.HttpContext.Request.Headers.Authorization
            .ToString()
            .Replace("Bearer ", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(token) || !await administration.IsSessionValidAsync(token, context.HttpContext.RequestAborted))
        {
            return Results.Unauthorized();
        }

        return await next(context);
    }
}
