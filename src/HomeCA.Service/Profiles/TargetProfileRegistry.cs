using System.Text.Json;
using System.Text.RegularExpressions;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Profiles;

public sealed class TargetProfileRegistry(HomeCaStorage storage)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex ProfileIdPattern = new("^[a-z0-9-]+$", RegexOptions.Compiled);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path = Path.Combine(storage.RootPath, "profiles", "profiles.json");

    public async Task<IReadOnlyList<TargetProfile>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadUnsafeAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TargetProfile> AddAsync(CreateTargetProfileRequest request, CancellationToken cancellationToken)
    {
        Validate(request);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = await ReadUnsafeAsync(cancellationToken);
            if (profiles.Any(profile => string.Equals(profile.Id, request.Id, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("A profile with this ID already exists.");
            }

            var created = Map(request);
            profiles.Add(created);
            await WriteUnsafeAsync(profiles, cancellationToken);
            return created;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<TargetProfile?> UpdateAsync(string id, UpdateTargetProfileRequest request, CancellationToken cancellationToken)
    {
        Validate(request);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = await ReadUnsafeAsync(cancellationToken);
            var index = profiles.FindIndex(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return null;

            profiles[index] = new TargetProfile(
                profiles[index].Id,
                request.Version.Trim(),
                request.DisplayName.Trim(),
                request.Purpose.Trim(),
                NormalizeKeyAlgorithm(request.KeyAlgorithm),
                NormalizeExportFormats(request.ExportFormats),
                NormalizeValidation(request.Validation),
                request.Documentation.Trim(),
                request.RenewalScriptTemplate.Trim());

            await WriteUnsafeAsync(profiles, cancellationToken);
            return profiles[index];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var profiles = await ReadUnsafeAsync(cancellationToken);
            var index = profiles.FindIndex(profile => string.Equals(profile.Id, id, StringComparison.OrdinalIgnoreCase));
            if (index < 0) return false;
            if (profiles.Count == 1)
            {
                throw new InvalidOperationException("At least one target profile must remain available.");
            }

            profiles.RemoveAt(index);
            await WriteUnsafeAsync(profiles, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<TargetProfile>> ReadUnsafeAsync(CancellationToken cancellationToken)
    {
        EnsureSeed();
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<TargetProfile>>(stream, Json, cancellationToken) ?? [];
    }

    private async Task WriteUnsafeAsync(List<TargetProfile> profiles, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, profiles.OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(), Json, cancellationToken);
    }

    private void EnsureSeed()
    {
        if (!File.Exists(_path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.Copy(Path.Combine(AppContext.BaseDirectory, "profiles.json"), _path);
        }
    }

    private static TargetProfile Map(CreateTargetProfileRequest request) =>
        new(
            request.Id.Trim(),
            request.Version.Trim(),
            request.DisplayName.Trim(),
            request.Purpose.Trim(),
            NormalizeKeyAlgorithm(request.KeyAlgorithm),
            NormalizeExportFormats(request.ExportFormats),
            NormalizeValidation(request.Validation),
            request.Documentation.Trim(),
            request.RenewalScriptTemplate.Trim());

    private static void Validate(CreateTargetProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id) || !ProfileIdPattern.IsMatch(request.Id.Trim()))
        {
            throw new ArgumentException("Profile ID must contain only lowercase letters, numbers, and hyphens.");
        }

        ValidateCore(request.Version, request.DisplayName, request.Purpose, request.KeyAlgorithm, request.ExportFormats, request.Validation, request.Documentation);
    }

    private static void Validate(UpdateTargetProfileRequest request) =>
        ValidateCore(request.Version, request.DisplayName, request.Purpose, request.KeyAlgorithm, request.ExportFormats, request.Validation, request.Documentation);

    private static void ValidateCore(string version, string displayName, string purpose, string keyAlgorithm, IReadOnlyList<string> exportFormats, ProfileValidationRequest validation, string documentation)
    {
        if (string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Profile version is required.");
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Profile name is required.");
        if (string.IsNullOrWhiteSpace(purpose)) throw new ArgumentException("Profile purpose is required.");
        if (string.IsNullOrWhiteSpace(documentation)) throw new ArgumentException("Profile documentation is required.");
        if (validation.MaximumValidityDays is < 1 or > 730) throw new ArgumentException("Maximum validity must be between 1 and 730 days.");
        if (validation.RequiresDnsName && validation.MaximumValidityDays < 1) throw new ArgumentException("Profile validation is invalid.");
        if (!NormalizeExportFormats(exportFormats).Any()) throw new ArgumentException("At least one export format is required.");
        _ = NormalizeKeyAlgorithm(keyAlgorithm);
    }

    private static string NormalizeKeyAlgorithm(string keyAlgorithm)
    {
        if (string.Equals(keyAlgorithm, "RSA", StringComparison.OrdinalIgnoreCase)) return "RSA";
        if (string.Equals(keyAlgorithm, "ECC", StringComparison.OrdinalIgnoreCase)) return "ECC";
        throw new ArgumentException("Key algorithm must be ECC or RSA.");
    }

    private static IReadOnlyList<string> NormalizeExportFormats(IReadOnlyList<string> exportFormats)
    {
        var formats = exportFormats
            .Where(format => !string.IsNullOrWhiteSpace(format))
            .Select(format => format.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (formats.Count == 0) return [];
        return formats;
    }

    private static ProfileValidation NormalizeValidation(ProfileValidationRequest validation) =>
        new(validation.RequiresDnsName, validation.AllowsIpAddress, validation.MaximumValidityDays);
}

public sealed record TargetProfile(string Id, string Version, string DisplayName, string Purpose, string KeyAlgorithm, IReadOnlyList<string> ExportFormats, ProfileValidation Validation, string Documentation, string RenewalScriptTemplate);
public sealed record ProfileValidation(bool RequiresDnsName, bool AllowsIpAddress, int MaximumValidityDays);
public sealed record CreateTargetProfileRequest(string Id, string Version, string DisplayName, string Purpose, string KeyAlgorithm, IReadOnlyList<string> ExportFormats, ProfileValidationRequest Validation, string Documentation, string RenewalScriptTemplate);
public sealed record UpdateTargetProfileRequest(string Version, string DisplayName, string Purpose, string KeyAlgorithm, IReadOnlyList<string> ExportFormats, ProfileValidationRequest Validation, string Documentation, string RenewalScriptTemplate);
public sealed record ProfileValidationRequest(bool RequiresDnsName, bool AllowsIpAddress, int MaximumValidityDays);
