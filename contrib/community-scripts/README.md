# community-scripts submission

Prepared script set for submitting HomeCA to
[community-scripts](https://community-scripts.org/) — the project behind the
Proxmox VE Helper-Scripts.

These files are **not used by HomeCA itself**. They are kept here so the
submission is ready to go the moment the eligibility criteria are met. HomeCA's
own Proxmox one-liner remains [`deploy/scripts/homeca-lxc.sh`](../../deploy/scripts/homeca-lxc.sh).

## Status: not yet eligible

New scripts are submitted to
[ProxmoxVED](https://github.com/community-scripts/ProxmoxVED), not ProxmoxVE.
Its pull request template contains a machine-validated "Application
Requirements" section:

| Requirement | HomeCA (as of 2026-09-08) |
|---|---|
| Application is at least 6 months old | ❌ created 2026-08-28 — eligible from **2027-02-28** |
| Application is actively maintained | ✅ |
| Application has 600+ GitHub stars | ❌ **2** |
| Official release tarballs are published | ✅ since `v0.3.8` |
| Bare-metal install, no Docker, no `git pull` | ✅ |

> Pull requests that do not meet these requirements may be closed without review.

The star count is the hard blocker. Do not open the PR before it is met.

## Open items before submitting

- [ ] **Logo.** `json/homeca.json` has a placeholder in the `logo` field. Submit
      an icon to [selfh.st/icons](https://selfh.st/icons/) first, then point the
      field at `https://cdn.jsdelivr.net/gh/selfhst/icons@main/webp/homeca.webp`.
      HomeCA currently ships no logo asset at all.
- [ ] **Test URL.** Open a "New Script" issue in ProxmoxVED, then uncomment
      `var_testurl` in `ct/homeca.sh` and point it at that issue.
- [ ] **`date_created`.** Set to the actual submission date.
- [ ] **Test on real hardware.** Neither script has been run against a Proxmox
      host yet. This is a hard requirement — see below.
- [ ] **arm64.** `var_arm64` is set to `no` because the release workflow only
      publishes `linux-x64`. If arm64 builds are added later, change both
      `var_arm64` and the `architectures` array in the JSON.

## Deliberate deviations from HomeCA's own installer

These were made to satisfy community-scripts' coding standards
([AGENTS.md](https://github.com/community-scripts/ProxmoxVED/blob/main/AGENTS.md)).
Review them before submitting — the first one is a security trade-off.

1. **Runs as `root`, not as the `homeca` service user.** Creating dedicated
   system users is an explicit anti-pattern there ("LXC containers run as root,
   no separate user needed"). The container itself is the security boundary.
   The systemd hardening (`ProtectSystem=strict`, `ProtectHome`, `PrivateTmp`,
   scoped `ReadWritePaths`) is kept from the upstream unit to compensate.
2. **No sudoers drop-in.** `deploy/sudoers/homeca-tls` exists only to let the
   unprivileged `homeca` user escalate for TLS activation. Running as root makes
   the `sudo` calls in `SetupEndpoints` work unconditionally, so the file is
   unnecessary.
3. **Debian 13, not Debian 12.** Verified that
   `packages.microsoft.com/debian/13/prod` carries `aspnetcore-runtime-10.0`.
4. **Installs `homeca-linux-x64.tar.gz`, not `homeca-release-bundle.tar.gz`.**
   The former unpacks straight into `/opt/homeca`; the latter nests everything
   under `app/` and additionally carries deploy assets this script provides
   itself.
5. **.NET comes from `setup_dotnet`,** not a hand-rolled
   `packages-microsoft-prod.deb` install. Custom runtime installation is an
   anti-pattern there.
6. **Configuration via systemd `Environment=`,** so `/opt/homeca` stays
   disposable and `CLEAN_INSTALL=1` can replace it wholesale on update.
7. **`HOME` is pinned to `/var/lib/homeca`.** A consequence of (1): HomeCA does
   not configure ASP.NET Core Data Protection explicitly, so the keyring lands
   in `$HOME/.aspnet/DataProtection-Keys`. Under the upstream unit `$HOME` is the
   `homeca` user's home and works out; as root it would be `/root`, which
   `ProtectHome=true` blocks, and Data Protection would silently fall back to
   ephemeral keys — logging every user out on each restart.

   Worth considering upstream regardless: calling `PersistKeysToFileSystem()`
   with an explicit path under `Storage:RootPath` would make this independent of
   who runs the service.

## Submission process

1. Fork [ProxmoxVED](https://github.com/community-scripts/ProxmoxVED).
2. Branch: `feat/homeca`.
3. Copy the files into the fork, preserving layout:
   - `ct/homeca.sh`
   - `install/homeca-install.sh`
   - `json/homeca.json`
   Do **not** create `ct/headers/homeca` — CI generates it.
4. Test against a real Proxmox VE host: fresh install, reboot, and an update run
   through `update_script`.
5. Open the PR. Tick the "New script" type, fill in the Application
   Requirements section honestly, and — since these files were drafted with AI
   assistance — tick **"AI was used"** and name the model. That box is mandatory
   and scripts that are clearly AI-generated without human revision may be
   closed unreviewed.
6. Maintainers promote accepted scripts from ProxmoxVED to ProxmoxVE via a
   `Migration To ProxmoxVE` label. Only then does the entry appear on
   community-scripts.org.
