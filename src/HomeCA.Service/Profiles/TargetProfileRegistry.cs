using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Profiles;

public sealed class TargetProfileRegistry(HomeCaStorage storage)
{
    private readonly string _path = Path.Combine(storage.RootPath, "profiles", "profiles.json");
    public async Task<IReadOnlyList<TargetProfile>> ListAsync(CancellationToken cancellationToken) => File.Exists(_path) ? await JsonSerializer.DeserializeAsync<List<TargetProfile>>(File.OpenRead(_path), cancellationToken: cancellationToken) ?? [] : [];
}

public sealed record TargetProfile(string Id, string Version, string DisplayName, string KeyAlgorithm, IReadOnlyList<string> ExportFormats, string Documentation, string RenewalScriptTemplate);
