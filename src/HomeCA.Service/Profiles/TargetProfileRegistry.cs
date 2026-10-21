using System.Security.Cryptography;
using System.Text;
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
    private readonly string _seedStatePath = Path.Combine(storage.RootPath, "profiles", "seed-state.json");
    private bool _seedReconciled;

    // SHA-256 over the text fields of every built-in profile shipped before profiles carried a script type and
    // localizations. A stored profile matching one of these was never edited and is replaced by the current seed.
    private static readonly HashSet<string> LegacySeedFingerprints = new(StringComparer.Ordinal)
    {
        "generic-tls:53ce1cbea22dc2fae193f9de433b88ba109ea0bab4fa7246237e7fd9d60053a9",
        "generic-tls:7cc342301fc2b535d09481eb3d4db178fbdbc782e47e3831fd58282daf0afbe0",
        "generic-tls:7a70e98078cff28af6fcad83a37dda5ea8fd4a347e706f228be96287c41dd254",
        "iis:51e9a5358ced34333aeef587e8c8d37a24232f1a0e0020e872c049b2bf19d751",
        "iis:6217a97870e1228618a672c94df9cbbffab8b3660b7ba73a2a3082dacbce35cb",
        "proxmox:dc460f8be12cb83f9f082c212e99433105d146bf38fee5b2d9ac75acbec71166",
        "proxmox:3d47c2aa935aafb58d55740965e8e07ac93bf6cd89bc8236638d69035bed94f6",
        "opnsense:fdffe54b3e4b0f46e72674fd9f32b56e8d27683cfc75aa6076af35b88d57b09c",
        "home-assistant:da94bf62686c396333021f91446a014a39c030225209daeed4ee44853461209b",
        "home-assistant:5138f95f5a966aa9710d4e00c5fc913c86efcc68d06f886a8fe806711350bb08",
        "unifi:2d41732c9c149063594f1e97eb3cfdfa0c670368b75933e59e50cb84d04175a2",
        "haproxy:224523c30d139228acbc040303d4d53199e7db98cdf3fc5c9867082be3683b71",
        "nginx:592b9af693d860cdfea9603fc2ba46619c65504d3b32c530f7f1ad1f756579ee",
        "cisco-switch:9f49bb233587c98c4a538e954f81777d5c4d7c4b998bb596940d3dff1e886e0e",
        "huawei-switch:3fdaa6307d06e662c7bfc0ae2b6119d62295b40f1b3e282940ddeb7ea26fbd1c",
        "synology:2c44e9906cc63a540f695b7f50031f29221f7df5fa0d31a305c4471164d94857",
        "teamcity:f617bb42b9f4bb117b43c009b5c7f0a9310ad02fedc506798a586f0282c1d254"
    };

    // SHA-256 over the text fields of built-in profiles whose script later gained explicit backups or ran unattended. A stored profile
    // matching one of these was never edited and is replaced by the current seed.
    private static readonly HashSet<string> PreviousSeedFingerprints = new(StringComparer.Ordinal)
    {
        "unifi:d116b25cf6a304bcdea7e4d580e003f8d7deb9d0d44c40092399056602db82d7",
        "teamcity:94d9566413543842d61bdb40290ce6c9e5a1706cec4e72a8aa5a12192c127314",
        "unraid:0d73d9494f42c20e377252076eb65431d5d062d4d3a3b57329e28276484df9dd",
        "vmware-esxi:d825b2ea29044e9cebfa5608608868bb2af8806fdcef2ce56839877160b3a961",
        "jellyfin:9663de05cce690ed192a31749e6deaf5a943abe5a6f1856471679ebc8bfb4c83",
        "iis:3bc80a2652358fe4f56c818f1100f28f69e42915c77295fdd67f2b6f5b43e4f4"
    };

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

            var existing = profiles[index];
            var updated = new TargetProfile(
                existing.Id,
                request.Version.Trim(),
                request.DisplayName.Trim(),
                request.Purpose.Trim(),
                NormalizeKeyAlgorithm(request.KeyAlgorithm),
                NormalizeExportFormats(request.ExportFormats),
                NormalizeValidation(request.Validation),
                request.Documentation.Trim(),
                request.RenewalScriptTemplate.Trim(),
                NormalizeScriptType(request.ScriptType));
            profiles[index] = updated with
            {
                Localizations = request.Localizations is not null
                    ? NormalizeLocalizations(request.Localizations)
                    : KeepUnchangedLocalizations(existing, updated)
            };

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
        List<TargetProfile> profiles;
        await using (var stream = File.OpenRead(_path))
            profiles = await JsonSerializer.DeserializeAsync<List<TargetProfile>>(stream, Json, cancellationToken) ?? [];

        // The stored file only changes through this registry, so the seed is reconciled once per process.
        if (_seedReconciled) return profiles;
        var migrated = await MigrateLegacySeedProfilesAsync(profiles, cancellationToken);
        var added = await AddNewSeedProfilesAsync(profiles, cancellationToken);
        if (migrated || added) await WriteUnsafeAsync(profiles, cancellationToken);
        _seedReconciled = true;
        return profiles;
    }

    /// <summary>
    /// Adds built-in profiles that a newer release ships and this installation has never seen. Profiles the
    /// administrator deleted stay deleted, because every seed ID ever offered is remembered.
    /// </summary>
    private async Task<bool> AddNewSeedProfilesAsync(List<TargetProfile> profiles, CancellationToken cancellationToken)
    {
        var seed = await ReadSeedAsync(cancellationToken);
        HashSet<string> offered;
        if (File.Exists(_seedStatePath))
        {
            await using var stream = File.OpenRead(_seedStatePath);
            offered = (await JsonSerializer.DeserializeAsync<SeedState>(stream, Json, cancellationToken))?.OfferedIds.ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            // Before this file existed, the stored profiles and the original twelve were all that had been offered.
            offered = profiles.Select(profile => profile.Id)
                .Concat(LegacySeedFingerprints.Select(fingerprint => fingerprint[..fingerprint.IndexOf(':')]))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var additions = seed.Where(profile => !offered.Contains(profile.Id) && !profiles.Any(existing => existing.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))).ToList();
        profiles.AddRange(additions);
        var allOffered = offered.Concat(seed.Select(profile => profile.Id)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        if (!File.Exists(_seedStatePath) || allOffered.Count != offered.Count)
        {
            var temporaryPath = _seedStatePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, new SeedState(allOffered), Json, cancellationToken);
            File.Move(temporaryPath, _seedStatePath, true);
        }
        return additions.Count > 0;
    }

    private static async Task<List<TargetProfile>> ReadSeedAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(SeedPath);
        return await JsonSerializer.DeserializeAsync<List<TargetProfile>>(stream, Json, cancellationToken) ?? [];
    }

    private sealed record SeedState(IReadOnlyList<string> OfferedIds);

    /// <summary>Replaces unedited built-in profiles from older releases with the current, localized seed.</summary>
    private static async Task<bool> MigrateLegacySeedProfilesAsync(List<TargetProfile> profiles, CancellationToken cancellationToken)
    {
        if (!profiles.Any(IsUneditedLegacySeed)) return false;

        var seed = await ReadSeedAsync(cancellationToken);
        var migrated = false;
        for (var index = 0; index < profiles.Count; index++)
        {
            if (!IsUneditedLegacySeed(profiles[index])) continue;
            var replacement = seed.FirstOrDefault(item => item.Id == profiles[index].Id);
            if (replacement is null) continue;
            profiles[index] = replacement;
            migrated = true;
        }

        return migrated;
    }

    private static bool IsUneditedLegacySeed(TargetProfile profile) =>
        (profile.ScriptType is null && profile.Localizations is null && LegacySeedFingerprints.Contains(Fingerprint(profile)))
        || PreviousSeedFingerprints.Contains(Fingerprint(profile));

    internal static string Fingerprint(TargetProfile profile)
    {
        var text = string.Join('\u001f', profile.DisplayName, profile.Purpose, profile.Documentation, profile.RenewalScriptTemplate);
        return $"{profile.Id}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()}";
    }

    private static string SeedPath => Path.Combine(AppContext.BaseDirectory, "profiles.json");

    private async Task WriteUnsafeAsync(List<TargetProfile> profiles, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, profiles.OrderBy(profile => profile.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList(), Json, cancellationToken);
        File.Move(temporaryPath, _path, true);
    }

    private void EnsureSeed()
    {
        if (!File.Exists(_path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.Copy(SeedPath, _path);
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
            request.RenewalScriptTemplate.Trim(),
            NormalizeScriptType(request.ScriptType),
            request.Localizations is null ? null : NormalizeLocalizations(request.Localizations));

    private static void Validate(CreateTargetProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id) || !ProfileIdPattern.IsMatch(request.Id.Trim()))
        {
            throw new ArgumentException("Profile ID must contain only lowercase letters, numbers, and hyphens.");
        }

        ValidateCore(request.Version, request.DisplayName, request.Purpose, request.KeyAlgorithm, request.ExportFormats, request.Validation, request.Documentation);
        _ = NormalizeScriptType(request.ScriptType);
    }

    private static void Validate(UpdateTargetProfileRequest request)
    {
        ValidateCore(request.Version, request.DisplayName, request.Purpose, request.KeyAlgorithm, request.ExportFormats, request.Validation, request.Documentation);
        _ = NormalizeScriptType(request.ScriptType);
    }

    private static string? NormalizeScriptType(string? scriptType)
    {
        if (string.IsNullOrWhiteSpace(scriptType)) return null;
        var normalized = scriptType.Trim().ToLowerInvariant();
        if (!ProfileScripts.Types.Contains(normalized)) throw new ArgumentException("Script type must be sh, ps1 or cli.");
        return normalized;
    }

    private static IReadOnlyDictionary<string, ProfileTextLocalization>? NormalizeLocalizations(IReadOnlyDictionary<string, ProfileTextLocalization> localizations)
    {
        var normalized = localizations
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && entry.Value is not null)
            .Select(entry => (Culture: entry.Key.Trim().ToLowerInvariant(), Text: new ProfileTextLocalization(Clean(entry.Value.DisplayName), Clean(entry.Value.Purpose), Clean(entry.Value.Documentation))))
            .Where(entry => entry.Text.DisplayName is not null || entry.Text.Purpose is not null || entry.Text.Documentation is not null)
            .GroupBy(entry => entry.Culture)
            .ToDictionary(group => group.Key, group => group.Last().Text);
        return normalized.Count == 0 ? null : normalized;

        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Drops translations of a field whose base text was edited, so the UI never shows an outdated translation.</summary>
    private static IReadOnlyDictionary<string, ProfileTextLocalization>? KeepUnchangedLocalizations(TargetProfile existing, TargetProfile updated)
    {
        if (existing.Localizations is null) return null;
        return NormalizeLocalizations(existing.Localizations.ToDictionary(
            entry => entry.Key,
            entry => new ProfileTextLocalization(
                existing.DisplayName == updated.DisplayName ? entry.Value.DisplayName : null,
                existing.Purpose == updated.Purpose ? entry.Value.Purpose : null,
                existing.Documentation == updated.Documentation ? entry.Value.Documentation : null)));
    }

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

public sealed record TargetProfile(string Id, string Version, string DisplayName, string Purpose, string KeyAlgorithm, IReadOnlyList<string> ExportFormats, ProfileValidation Validation, string Documentation, string RenewalScriptTemplate, string? ScriptType = null, IReadOnlyDictionary<string, ProfileTextLocalization>? Localizations = null);
/// <summary>Translated display texts of a profile; the base fields of <see cref="TargetProfile"/> are the English fallback.</summary>
public sealed record ProfileTextLocalization(string? DisplayName = null, string? Purpose = null, string? Documentation = null);
public sealed record ProfileValidation(bool RequiresDnsName, bool AllowsIpAddress, int MaximumValidityDays);
public sealed record CreateTargetProfileRequest(string Id, string Version, string DisplayName, string Purpose, string KeyAlgorithm, IReadOnlyList<string> ExportFormats, ProfileValidationRequest Validation, string Documentation, string RenewalScriptTemplate, string? ScriptType = null, IReadOnlyDictionary<string, ProfileTextLocalization>? Localizations = null);
public sealed record UpdateTargetProfileRequest(string Version, string DisplayName, string Purpose, string KeyAlgorithm, IReadOnlyList<string> ExportFormats, ProfileValidationRequest Validation, string Documentation, string RenewalScriptTemplate, string? ScriptType = null, IReadOnlyDictionary<string, ProfileTextLocalization>? Localizations = null);
public sealed record ProfileValidationRequest(bool RequiresDnsName, bool AllowsIpAddress, int MaximumValidityDays);
