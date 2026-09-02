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

        var session = await administration.ValidateSessionAsync(token, context.HttpContext.RequestAborted);
        if (!session.IsValid)
        {
            return Results.Unauthorized();
        }

        // The default administrator password is only safe long enough to establish a
        // session and replace it. Do not allow that session to operate the CA.
        if (session.MustChangePassword
            && !context.HttpContext.Request.Path.Equals("/api/v1/change-password", StringComparison.OrdinalIgnoreCase))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        return await next(context);
    }
}
