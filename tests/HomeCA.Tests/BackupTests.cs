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
    public async Task Verify_Rejects_Invalid_Filename()
    {
        var storage = _fixture.CreateStorage();

        await Assert.ThrowsAsync<ArgumentException>(() => storage.VerifyBackupAsync("../../etc/passwd", CancellationToken.None));
    }

    public void Dispose() => _fixture.Dispose();
}
