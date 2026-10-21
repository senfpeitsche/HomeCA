using System.Net;
using System.Net.Http.Json;
using HomeCA.Service.Resources;
using Microsoft.Extensions.Localization;

namespace HomeCA.Service.Components;

/// <summary>Turns an API problem response into a message for the snackbar.</summary>
public static class ApiError
{
    public static async Task<string> ReadAsync(HttpResponseMessage response, IStringLocalizer<UiText> text)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<Problem>();
            if (!string.IsNullOrWhiteSpace(problem?.Detail)) return problem.Detail;
            if (problem?.Errors?.Values.FirstOrDefault()?.FirstOrDefault() is { } error) return error;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException)
        {
            // Not a problem document; fall back to a generic message.
        }
        return response.StatusCode == HttpStatusCode.Conflict ? text["ApiErrorConflict"] : text["ActionFailed"];
    }

    private sealed record Problem(string? Detail, Dictionary<string, string[]>? Errors);
}
