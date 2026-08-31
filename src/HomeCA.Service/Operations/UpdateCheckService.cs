using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace HomeCA.Service.Operations;

/// <summary>
/// Checks the GitHub Releases API for a newer HomeCA version.
/// The result is cached for 24 hours so the external call happens at most once per day.
/// </summary>
public sealed class UpdateCheckService
{
    private const string GitHubReleasesUrl = "https://api.github.com/repos/senfpeitsche/HomeCA/releases/latest";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly string _currentVersion;

    private UpdateCheckResult? _cached;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public UpdateCheckService(IHttpClientFactory httpClientFactory, ILogger<UpdateCheckService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        var informational = Assembly.GetEntryAssembly()!
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        _currentVersion = informational.Split('+')[0];
    }

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheDuration)
            return _cached;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring the lock
            if (_cached is not null && DateTimeOffset.UtcNow - _cachedAt < CacheDuration)
                return _cached;

            var result = await FetchLatestReleaseAsync(cancellationToken);
            _cached = result;
            _cachedAt = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<UpdateCheckResult> FetchLatestReleaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HomeCA-UpdateCheck/1.0");
            client.Timeout = TimeSpan.FromSeconds(10);

            var release = await client.GetFromJsonAsync<GitHubRelease>(GitHubReleasesUrl, cancellationToken);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
                return new UpdateCheckResult(_currentVersion, null, false, null);

            var latestVersion = release.TagName.TrimStart('v');

            var isNewer = IsNewerVersion(latestVersion, _currentVersion);
            _logger.LogInformation("Update check: current={Current}, latest={Latest}, updateAvailable={Available}",
                _currentVersion, latestVersion, isNewer);

            return new UpdateCheckResult(_currentVersion, latestVersion, isNewer, release.HtmlUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Update check failed — will retry after cache expiry");
            return new UpdateCheckResult(_currentVersion, null, false, null);
        }
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        if (Version.TryParse(NormalizeVersion(latest), out var latestParsed) &&
            Version.TryParse(NormalizeVersion(current), out var currentParsed))
        {
            return latestParsed > currentParsed;
        }
        return false;
    }

    /// <summary>Pads a version string to at least Major.Minor so <see cref="Version.TryParse"/> succeeds.</summary>
    private static string NormalizeVersion(string v)
    {
        // Strip any pre-release suffix (e.g. "1.2.3-beta1" → "1.2.3")
        var dashIndex = v.IndexOf('-');
        if (dashIndex >= 0) v = v[..dashIndex];
        return v.Count(c => c == '.') == 0 ? v + ".0" : v;
    }

    private sealed record GitHubRelease(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl);
}

public sealed record UpdateCheckResult(
    string CurrentVersion,
    string? LatestVersion,
    bool UpdateAvailable,
    string? ReleaseUrl);
