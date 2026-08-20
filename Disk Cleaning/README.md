# Disk Cleaning

Two standalone disk-cleanup scripts, one per OS. Both are safe by default
(nothing irreversible happens unless you opt in with a flag) and both
support a dry run so you can preview what would be deleted before it
actually happens.

| Script | Platform | Requires |
|---|---|---|
| [`Clean-Disk.ps1`](./Clean-Disk.ps1) | Windows 10 / 11 | PowerShell 5.1+ (built in) |
| [`clean-disk.sh`](./clean-disk.sh) | Arch Linux | bash, coreutils, `numfmt` |

Neither script needs anything installed beyond what ships with the OS,
except the optional `pacman-contrib` package on Arch (see below).

---

## `Clean-Disk.ps1` — Windows 10 / 11

Reclaims space from essentially every place Windows hoards it: temp folders,
servicing and update caches, shader/font/thumbnail caches, crash dumps, logs,
the Recycle Bin, upgrade rollback folders, the hibernation file, restore
points, the search index, event logs, the WinSxS component store, and
browser / app / developer caches.

Full parameter docs are also available in-shell:

```powershell
Get-Help .\Clean-Disk.ps1 -Full
```

### Usage

```powershell
# Preview only — deletes nothing, prints what each item would free
.\Clean-Disk.ps1 -DryRun

# Standard clean (regenerable scratch data only)
.\Clean-Disk.ps1

# Run elevated for system-level items. Right-click PowerShell > Run as
# Administrator, or:
Start-Process powershell -Verb RunAs -ArgumentList '-File', '.\Clean-Disk.ps1'

# Typical "reclaim real space" run
.\Clean-Disk.ps1 -IncludeBrowserCache -IncludeAppCaches -IncludeDevCaches

# Free the hibernation file (usually 40-100% of installed RAM)
.\Clean-Disk.ps1 -Hibernation Off

# Everything, unattended. Irreversible — read the flag table first.
.\Clean-Disk.ps1 -All -Hibernation Off -Yes
```

You may need to allow script execution once per machine:

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
```

### Flags

| Flag | Default | Effect |
|---|---|---|
| `-DryRun` | off | Show what would be deleted and how much space would be freed; deletes nothing. |
| `-All` | off | Turn on every deletion-based opt-in below, **including `-Aggressive`**. Does not touch hibernation or Compact OS, since those change how the machine behaves rather than just deleting junk. |
| `-IncludeBrowserCache` | off | Cache / code cache / GPU cache for Chrome, Edge, Brave, Vivaldi, Opera, Chromium, Yandex (every profile) and Firefox. Never history, passwords, bookmarks, cookies or sessions. |
| `-IncludeAppCaches` | off | Teams, Discord, Slack, Spotify, Steam, Epic, Battle.net, Zoom, OneDrive logs, Adobe media cache, Microsoft Store. |
| `-IncludeDevCaches` | off | npm, yarn, pnpm, pip, NuGet, Gradle, Cargo, Go, Composer, vcpkg, Chocolatey, conda, VS Code, Visual Studio, JetBrains, Unity, Unreal, Android SDK. |
| `-IncludePrivacy` | off | Recent items, jump lists, browsing-history index, cookie caches. |
| `-IncludeWindowsOld` | off | Remove `C:\Windows.old`, `$Windows.~BT`, `$Windows.~WS` and friends. **Irreversible** — no rollback to the previous Windows build. |
| `-IncludeComponentCleanup` | off | `DISM /StartComponentCleanup` to compact WinSxS. Safe alone; `-Aggressive` adds `/ResetBase` and `/SPSuperseded`. Slow. |
| `-IncludeShadowCopies` | off | Delete VSS shadow copies (System Restore points), keeping the newest per volume. `-Aggressive` deletes all of them. |
| `-IncludeEventLogs` | off | Clear every Windows event log. Destroys audit/troubleshooting history. |
| `-IncludeSearchIndex` | off | Delete `Windows.edb` and let the index rebuild. Often several GB. |
| `-RunDiskCleanup` | off | Also drive built-in `cleanmgr.exe` with all handlers enabled, to catch anything this script does not know about. The Downloads handler is always left disabled. Slow. |
| `-Hibernation` | `Keep` | `Reduce` shrinks `hiberfil.sys` to ~40% of RAM (keeps Fast Startup, loses full hibernate). `Off` deletes it entirely (loses both). |
| `-CompactOS` | off | `compact /compactos:always` — compresses system binaries. Reversible with `compact /compactos:never`. Takes 10-20 minutes. |
| `-Aggressive` | off | Escalates the opt-ins above to their irreversible variants and adds the MSI patch cache, `catroot2`, `Config.Msi`, the Office upload cache, and package stores (`.nuget`, `.m2`, Go modules, `docker system prune`). |
| `-Yes` | off | Skip the confirmation prompt before irreversible steps. |
| `-Quiet` | off | Suppress per-item output; print only the summary. |

### What it cleans

**Always (no elevation needed):**

- User temp, `LocalAppData\Temp`, LocalLow temp
- `INetCache`, `WebCache`, `Windows\Caches`, `CryptnetUrlCache`
- Thumbnail and icon caches (`thumbcache_*.db`, `iconcache_*.db`)
- Windows Error Reporting and user crash dumps
- Shader caches: D3D, NVIDIA (DX + GL), AMD, Intel
- Remote Desktop bitmap cache
- UWP/Store app `INetCache`, `TempState`, `AC\Temp`
- Recycle Bin, across every local drive

**Elevated, additionally:**

- `C:\Windows\Temp`, `C:\Temp`, Prefetch, PerfLogs, Downloaded Program Files
- Windows Update download cache and Delivery Optimization cache (the
  `wuauserv`, `bits` and `dosvc` services are stopped and restarted around it)
- System-profile INetCache and CryptnetUrlCache, service font cache,
  Temporary ASP.NET Files
- Servicing and setup logs: CBS, DISM, WindowsUpdate, MoSetup, SIH,
  waasmedic, Panther, `setupapi.*.log`, debug and security logs, and
  `System32\LogFiles` entries older than 7 days
- Minidumps, `MEMORY.DMP`, LiveKernelReports, WER queue/archive,
  diagnostics ETL/RBS traces, Defender scan history and superseded
  definition deltas
- Upgrade leftovers: `$GetCurrent`, `$SysReset`, `$WinREAgent`, `ESD`,
  `Windows10Upgrade` — always safe, these are pure setup scratch space
- Every other local user profile's temp, INetCache, WER, crash dumps and
  thumbnails

**Opt-in tiers** are listed in the flag table above.

### Output

Each item prints as it is processed (`[clean]`, `[remove]`, `[locked]`,
`[skip]`, `[guard]`), then a summary sorted by size, the real free-space
delta on the system drive, and a **"Large items left alone"** section
reporting `pagefile.sys`, `swapfile.sys`, `hiberfil.sys`, your Downloads
folder and shadow-copy storage — space worth knowing about, but not
something a script should delete for you.

### Notes

- **Safety rails.** A protected-path list blocks any target that resolves to
  a drive root, `C:\Windows`, `System32`, `WinSxS`, `Program Files`,
  `ProgramData`, a user profile root, Documents, Desktop or Downloads.
  Targets must be absolute local paths with at least one component below the
  root, so an undefined environment variable can never collapse a target into
  something dangerous.
- **No double counting.** Targets that resolve to the same folder
  (`$env:TEMP` and `LocalAppData\Temp` are usually identical) are cleaned once.
- **Locked files are skipped** and reported as `[locked]`, with the size still
  in use. This is normal — a running process is holding them open.
- **Native tools are measured by free-space delta.** DISM, `cleanmgr`,
  `vssadmin` and `compact` do not report what they reclaimed, so the script
  diffs volume free space around them. If something else writes to the disk
  at the same time, those particular numbers are approximate.
- `Windows.old` and other rollback folders need `takeown` + `icacls` first;
  the script does that automatically.
- Anything irreversible prompts once, up front, listing every destructive
  step it is about to take. `-Yes` skips the prompt; in a non-interactive
  session without `-Yes` the script aborts rather than guessing.

### Keep this file ASCII

`Clean-Disk.ps1` is saved as **UTF-8 with a BOM and an ASCII-only body**, and
it should stay that way.

PowerShell 5.1 — the version that ships with Windows — reads a BOM-less
`.ps1` using the system ANSI codepage, not UTF-8. A UTF-8 em dash is the
bytes `E2 80 94`, which Windows-1252 decodes as `â€”`, and that third
character is U+201D: a curly closing double quote, **which PowerShell accepts
as a real string delimiter**. So a line like

```powershell
Write-Info "  [skip] $Name — path not found ($Path)"
```

ends its string early, leaves `($Path)` unbalanced, and produces a cascade of
misleading errors pointing at unrelated lines:

```
Missing closing ')' in expression.
Missing closing '}' in statement block or type definition.
Unexpected token ')' in expression or statement.
```

The BOM alone fixes it, but an ASCII-only body means the file cannot break
this way even if the BOM is later lost to a careless editor or a `git` filter.

---

## `clean-disk.sh` — Arch Linux

Arch doesn't accumulate cruft the way Windows does — no WinSxS, no
`Windows.old`, no monthly cumulative-update bloat — but three things do
grow unbounded if left alone: the pacman package cache, the systemd
journal, and orphaned/AUR-helper caches. This script handles those plus
the usual user-level junk (trash, thumbnails, stale `~/.cache` entries).

### Usage

```bash
chmod +x clean-disk.sh   # already executable if cloned with permissions intact

# Preview only — deletes nothing
./clean-disk.sh --dry-run

# Standard clean, user-level items only (no root needed)
./clean-disk.sh

# Full clean including pacman cache, orphans, journal, coredumps (needs root)
sudo ./clean-disk.sh

# Skip confirmation prompts (e.g. for orphan package removal)
sudo ./clean-disk.sh --yes

# Deeper clean: full pacman cache wipe, docker prune, npm/pip/go/cargo caches
sudo ./clean-disk.sh --aggressive --yes
```

### Flags

| Flag | Default | Effect |
|---|---|---|
| `--dry-run` | off | Show what would be removed and how much space would be freed; deletes nothing. |
| `--yes` | off | Don't prompt for confirmation on destructive steps (orphan removal, `pacman -Scc`, `docker system prune`). |
| `--aggressive` | off | Also: full pacman cache wipe (`pacman -Scc` — **no downgrade safety net**), `docker system prune -af --volumes` (**deletes all stopped containers/unused images/volumes**), and dev tool caches (npm, pip, Go build cache, cargo registry cache). |
| `--user-only` | off | Skip everything that needs root (pacman cache, orphans, journal, coredumps, `/var/tmp`), even if run as root. |
| `-h`, `--help` | — | Show usage. |

### What it cleans

**Without root**, only user-level items run:
- Thumbnail cache, Trash, yay/paru cache
- `~/.cache` entries untouched for 30+ days (not wiped wholesale — some
  tools, e.g. pip, pre-commit, treat `~/.cache` as real storage, not
  scratch space)
- Unused Flatpak runtimes

**With root** (`sudo ./clean-disk.sh`), also:
- Pacman package cache, trimmed to the last 3 versions of each package via
  `paccache` (install `pacman-contrib` for this — falls back to
  `pacman -Sc`, which only removes packages no longer installed, if it's
  missing)
- Orphaned packages (`pacman -Qtdq`), removed only after confirmation
- systemd journal, vacuumed to 2 weeks / 200 MB
- systemd coredumps, `/var/tmp` entries untouched for 10+ days

**With `--aggressive`**, additionally:
- Full pacman cache wipe (loses the ability to downgrade packages)
- `docker system prune -af --volumes`, if Docker is installed and running
- npm, pip, Go, and cargo build/package caches

### Notes

- Safe by default: the package cache keeps 3 versions per package (so you
  can still downgrade), and anything that discards otherwise-irrecoverable
  state is opt-in via `--aggressive`.
- Prompts (orphan removal, full cache wipe, docker prune) can be
  pre-approved with `--yes` for unattended/cron use.
- Reports free space on `/` before and after (skipped in `--dry-run` mode,
  since nothing changes).
