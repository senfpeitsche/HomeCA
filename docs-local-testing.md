# Local verification

Run a release build:

```powershell
dotnet build HomeCA.slnx -c Release
```

For a local Windows run, set `Storage__RootPath`, `Storage__BackupPath`, and `Storage__BackupKeyPath` to writable test paths, then execute `HomeCA.Service.dll`. The LXC-specific systemd unit is not required locally.

Before production use, verify CA initialization, administrator login, issuance, backup creation, and restore in an isolated environment.
