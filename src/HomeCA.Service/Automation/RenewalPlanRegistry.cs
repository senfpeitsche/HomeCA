using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Automation;

public sealed class RenewalPlanRegistry(HomeCaStorage storage, ILogger<RenewalPlanRegistry> logger)
{
    private readonly string _path = Path.Combine(storage.RootPath, "state", "renewal-plans.json");
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<IReadOnlyList<RenewalPlan>> ListAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await ReadUnsafeAsync(ct);
        }
        finally { _gate.Release(); }
    }

    public async Task<RenewalPlan> AddAsync(CreateRenewalPlanRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.CertificateId) || request.RenewBeforeDays is < 1 or > 365)
            throw new ArgumentException("Certificate and a renewal window between 1 and 365 days are required.");

        await _gate.WaitAsync(ct);
        try
        {
            var items = (await ReadUnsafeAsync(ct)).ToList();
            var plan = new RenewalPlan(Guid.NewGuid().ToString("N"), request.CertificateId, request.RenewBeforeDays, request.Enabled, DateTimeOffset.UtcNow);
            items.Add(plan);
            await WriteAtomicAsync(items, ct);
            logger.LogInformation("Added renewal plan {PlanId} for certificate {CertificateId}", plan.Id, plan.CertificateId);
            return plan;
        }
        finally { _gate.Release(); }
    }

    public async Task<RenewalPlan?> UpdateAsync(string id, UpdateRenewalPlanRequest request, CancellationToken ct)
    {
        if (request.RenewBeforeDays is < 1 or > 365)
            throw new ArgumentException("Renewal window must be between 1 and 365 days.");

        await _gate.WaitAsync(ct);
        try
        {
            var items = (await ReadUnsafeAsync(ct)).ToList();
            var index = items.FindIndex(plan => plan.Id == id);
            if (index < 0) return null;
            var existing = items[index];
            var updated = new RenewalPlan(existing.Id, request.CertificateId ?? existing.CertificateId, request.RenewBeforeDays, request.Enabled, existing.CreatedAt);
            items[index] = updated;
            await WriteAtomicAsync(items, ct);
            return updated;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var items = (await ReadUnsafeAsync(ct)).ToList();
            var removed = items.RemoveAll(plan => plan.Id == id);
            if (removed == 0) return false;
            await WriteAtomicAsync(items, ct);
            logger.LogInformation("Deleted renewal plan {PlanId}", id);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task<List<RenewalPlan>> ReadUnsafeAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return [];
        await using var stream = File.OpenRead(_path);
        return await JsonSerializer.DeserializeAsync<List<RenewalPlan>>(stream, cancellationToken: ct) ?? [];
    }

    private async Task WriteAtomicAsync<T>(T value, CancellationToken ct)
    {
        var temporaryPath = _path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
            await JsonSerializer.SerializeAsync(stream, value, cancellationToken: ct);
        File.Move(temporaryPath, _path, true);
    }
}

public sealed record CreateRenewalPlanRequest(string CertificateId, int RenewBeforeDays = 30, bool Enabled = true);
public sealed record UpdateRenewalPlanRequest(int RenewBeforeDays = 30, bool Enabled = true, string? CertificateId = null);
public sealed record RenewalPlan(string Id, string CertificateId, int RenewBeforeDays, bool Enabled, DateTimeOffset CreatedAt);
