namespace HomeCA.Tests;

public sealed class BackupTests : IDisposable
{
    private readonly TestFixture _fixture = new();

    [Fact]
    public async Task Create_And_Verify_Backup()
    {
        var storage = _fixture.CreateStorage();

        // Create a file in the data directory so the backup has content
        File.WriteAllText(Path.Combine(_fixture.RootPath, "state", "test.json"), "{}");

        var backup = await storage.CreateBackupAsync(CancellationToken.None);

        Assert.NotNull(backup);
        Assert.True(File.Exists(backup.Path));
        Assert.EndsWith(".hcab", backup.FileName);

        var verification = await storage.VerifyBackupAsync(backup.FileName, CancellationToken.None);
        Assert.True(verification.IsValid);
        Assert.True(verification.EntryCount > 0);
    }

    [Fact]
    public void Archive_Contains_State_Files_But_Not_In_Flight_Temporary_Files()
    {
        var storage = _fixture.CreateStorage();
        File.WriteAllText(Path.Combine(_fixture.RootPath, "state", "renewal-plans.json"), "[1]");
        File.WriteAllText(Path.Combine(_fixture.RootPath, "state", "renewal-plans.json.tmp"), "[1, 2");

        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(storage.CreateArchive(CancellationToken.None)));

        var entry = archive.GetEntry("state/renewal-plans.json");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        Assert.Equal("[1]", reader.ReadToEnd());
        Assert.DoesNotContain(archive.Entries, candidate => candidate.FullName.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backups_Created_In_Quick_Succession_Do_Not_Overwrite_Each_Other()
    {
        var storage = _fixture.CreateStorage();

        var first = await storage.CreateBackupAsync(CancellationToken.None);
        await Task.Delay(5);
        var second = await storage.CreateBackupAsync(CancellationToken.None);

        Assert.NotEqual(first.FileName, second.FileName);
        Assert.True((await storage.VerifyBackupAsync(first.FileName, CancellationToken.None)).IsValid);
        Assert.True((await storage.VerifyBackupAsync(second.FileName, CancellationToken.None)).IsValid);
    }

    [Fact]
    public async Task Verify_Rejects_Invalid_Filename()
    {
        var storage = _fixture.CreateStorage();

        await Assert.ThrowsAsync<ArgumentException>(() => storage.VerifyBackupAsync("../../etc/passwd", CancellationToken.None));
    }

    public void Dispose() => _fixture.Dispose();
}
