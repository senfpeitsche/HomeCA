using System.Text.Json;
using HomeCA.Service.Infrastructure;

namespace HomeCA.Service.Automation;
public sealed class RenewalPlanRegistry(HomeCaStorage storage)
{
    private readonly string _path=Path.Combine(storage.RootPath,"state","renewal-plans.json");
    public async Task<IReadOnlyList<RenewalPlan>> ListAsync(CancellationToken ct)=>File.Exists(_path)?await JsonSerializer.DeserializeAsync<List<RenewalPlan>>(File.OpenRead(_path),cancellationToken:ct)??[]:[];
    public async Task<RenewalPlan> AddAsync(CreateRenewalPlanRequest request,CancellationToken ct)
    {
        if(string.IsNullOrWhiteSpace(request.CertificateId)||request.RenewBeforeDays is < 1 or > 365)throw new ArgumentException("Certificate and a renewal window between 1 and 365 days are required.");
        var items=(await ListAsync(ct)).ToList();var plan=new RenewalPlan(Guid.NewGuid().ToString("N"),request.CertificateId,request.RenewBeforeDays,request.Enabled,DateTimeOffset.UtcNow);items.Add(plan);await using var stream=File.Create(_path);await JsonSerializer.SerializeAsync(stream,items,cancellationToken:ct);return plan;
    }
}
public sealed record CreateRenewalPlanRequest(string CertificateId,int RenewBeforeDays=30,bool Enabled=true);
public sealed record RenewalPlan(string Id,string CertificateId,int RenewBeforeDays,bool Enabled,DateTimeOffset CreatedAt);
