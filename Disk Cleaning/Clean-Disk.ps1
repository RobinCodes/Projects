#Requires -Version 5.1
<#
.SYNOPSIS
    Deep disk cleaner for Windows 10 / 11.

.DESCRIPTION
    Reclaims disk space across every location Windows is known to accumulate
    junk in: user and system temp, Windows Update and Delivery Optimization
    caches, Prefetch, thumbnail/icon/font/shader caches, crash dumps and
    minidumps, setup and servicing logs, Windows Error Reporting, the
    Recycle Bin (all drives), upgrade leftovers, Windows.old, the hibernation
    file, VSS shadow copies / restore points, the search index, event logs,
    the WinSxS component store, browser caches, app caches, and developer
    tool caches.

    Safe by default. Everything that loses recoverable state or changes how
    the machine behaves afterwards is OFF unless you opt in with a switch.
    A plain run only removes regenerable scratch data.

    ASCII-only on purpose: PowerShell 5.1 reads a BOM-less .ps1 as ANSI, and
    a UTF-8 em dash decodes to a curly quote that PS treats as a real string
    delimiter, which breaks parsing. Keep this file ASCII (it is also saved
    with a UTF-8 BOM as a second line of defence).

.PARAMETER DryRun
    Show what would be deleted and how much space would be freed. Deletes nothing.

.PARAMETER All
    Turn on every deletion-based opt-in below, including -Aggressive.
    Does NOT turn on -Hibernation or -CompactOS, because those change how the
    machine behaves afterwards rather than just deleting junk.

.PARAMETER IncludeBrowserCache
    Clear cache/code cache/GPU cache for Chrome, Edge, Brave, Vivaldi, Opera,
    Chromium, Yandex (all profiles) and Firefox. Never touches history,
    passwords, bookmarks, cookies or sessions.

.PARAMETER IncludeAppCaches
    Clear caches for Teams, Discord, Slack, Spotify, Steam, Epic, Zoom,
    OneDrive logs, Adobe media cache and Microsoft Store apps.

.PARAMETER IncludeDevCaches
    Clear npm, yarn, pnpm, pip, NuGet, Gradle, Cargo, Go, Composer, vcpkg,
    Chocolatey, conda, VS Code, Visual Studio, JetBrains, Unity and Unreal
    caches. With -Aggressive also clears package stores that force a full
    re-download (.nuget/packages, .m2/repository, go/pkg/mod) and runs
    "docker system prune".

.PARAMETER IncludePrivacy
    Clear recent-items lists, jump lists, the browsing history index and
    cookie caches. These are convenience/tracking artifacts rather than junk,
    so they are separate from the normal cleanup.

.PARAMETER IncludeWindowsOld
    Remove C:\Windows.old and other upgrade rollback folders. IRREVERSIBLE:
    you lose the ability to roll back to the previous Windows install.

.PARAMETER IncludeComponentCleanup
    Run DISM /StartComponentCleanup to compact WinSxS. Safe on its own; with
    -Aggressive it also passes /ResetBase and /SPSuperseded, which removes the
    ability to uninstall already-installed updates. Slow (minutes).

.PARAMETER IncludeShadowCopies
    Delete VSS shadow copies (System Restore points), keeping the most recent
    one per volume. With -Aggressive, deletes all of them including the newest.

.PARAMETER IncludeEventLogs
    Clear all Windows event logs. Destroys troubleshooting/audit history.

.PARAMETER IncludeSearchIndex
    Delete the Windows Search index (Windows.edb) and let it rebuild.
    Can free several GB; rebuilding takes time and CPU in the background.

.PARAMETER RunDiskCleanup
    Also drive the built-in cleanmgr.exe with every handler enabled (minus the
    Downloads folder, which is never touched). Catches anything this script
    does not know about. Slow (minutes).

.PARAMETER Hibernation
    Keep   - leave hibernation alone (default).
    Reduce - shrink hiberfil.sys to roughly 40% of RAM. Keeps Fast Startup,
             gives up full hibernate.
    Off    - delete hiberfil.sys entirely. Frees RAM-sized space but disables
             both hibernate and Fast Startup.

.PARAMETER CompactOS
    Compress Windows system binaries with "compact /compactos:always". Frees
    a few GB on small drives at a small CPU cost. Reversible with
    "compact /compactos:never". Slow (10-20 minutes).

.PARAMETER Aggressive
    Escalates the opt-ins above to their irreversible variants, and clears the
    MSI patch cache, catroot2, Config.Msi rollback data and the Office upload
    cache. See each parameter for what changes.

.PARAMETER Yes
    Do not prompt for confirmation before irreversible steps.

.PARAMETER Quiet
    Suppress per-item output; print only the final summary.

.EXAMPLE
    .\Clean-Disk.ps1 -DryRun
    Preview a standard clean without deleting anything.

.EXAMPLE
    .\Clean-Disk.ps1 -IncludeBrowserCache -IncludeAppCaches
    Standard clean plus browser and app caches.

.EXAMPLE
    .\Clean-Disk.ps1 -All -Hibernation Off -Yes
    Maximum cleanup, unattended. Irreversible.
#>

[CmdletBinding()]
param(
    [switch]$DryRun,
    [switch]$All,
    [switch]$IncludeBrowserCache,
    [switch]$IncludeAppCaches,
    [switch]$IncludeDevCaches,
    [switch]$IncludePrivacy,
    [switch]$IncludeWindowsOld,
    [switch]$IncludeComponentCleanup,
    [switch]$IncludeShadowCopies,
    [switch]$IncludeEventLogs,
    [switch]$IncludeSearchIndex,
    [switch]$RunDiskCleanup,
    [ValidateSet('Keep', 'Reduce', 'Off')]
    [string]$Hibernation = 'Keep',
    [switch]$CompactOS,
    [switch]$Aggressive,
    [switch]$Yes,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$script:TotalBytesFreed = 0
$script:Results         = @()
$script:LockedCount     = 0
$script:ErrorCount      = 0
$script:SeenPaths       = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)

if ($All) {
    $IncludeBrowserCache     = $true
    $IncludeAppCaches        = $true
    $IncludeDevCaches        = $true
    $IncludePrivacy          = $true
    $IncludeWindowsOld       = $true
    $IncludeComponentCleanup = $true
    $IncludeShadowCopies     = $true
    $IncludeEventLogs        = $true
    $IncludeSearchIndex      = $true
    $RunDiskCleanup          = $true
    $Aggressive              = $true
}

# --- Output helpers ---------------------------------------------------------

function Write-Info   { param([string]$Msg) if (-not $Quiet) { Write-Host $Msg -ForegroundColor Cyan } }
function Write-Detail { param([string]$Msg) if (-not $Quiet) { Write-Host $Msg -ForegroundColor Gray } }
function Write-Note   { param([string]$Msg) if (-not $Quiet) { Write-Host $Msg -ForegroundColor DarkGray } }

function Format-Bytes {
    param([long]$Bytes)
    if ($Bytes -ge 1TB) { return ('{0:N2} TB' -f ($Bytes / 1TB)) }
    if ($Bytes -ge 1GB) { return ('{0:N2} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N2} MB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N2} KB' -f ($Bytes / 1KB)) }
    return "$Bytes B"
}

function Add-Result {
    param([string]$Name, [long]$Freed)
    if ($Freed -lt 0) { $Freed = 0 }
    $script:TotalBytesFreed += $Freed
    $script:Results += [pscustomobject]@{ Item = $Name; Freed = $Freed }
}

# --- Environment ------------------------------------------------------------

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal($id)
    return $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-FreeSpace {
    try {
        $d = Get-CimInstance -ClassName Win32_LogicalDisk -Filter "DeviceID='$env:SystemDrive'" -ErrorAction Stop
        return [long]$d.FreeSpace
    } catch {
        try { return [long](Get-PSDrive -Name $env:SystemDrive.TrimEnd(':') -ErrorAction Stop).Free }
        catch { return 0 }
    }
}

function Get-RootFileSize {
    param([string]$FileName)
    try {
        $f = Get-ChildItem -LiteralPath "$env:SystemDrive\" -Force -File -ErrorAction SilentlyContinue |
             Where-Object { $_.Name -eq $FileName } | Select-Object -First 1
        if ($f) { return [long]$f.Length }
    } catch { }
    return 0
}

# Paths that must never have their contents wiped, no matter what a target
# expression expands to.
$script:ProtectedPaths = @(
    "$env:SystemDrive\", $env:WINDIR, "$env:WINDIR\System32", "$env:WINDIR\SysWOW64",
    "$env:WINDIR\WinSxS", "$env:WINDIR\Fonts", "$env:WINDIR\assembly",
    $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData,
    $env:USERPROFILE, "$env:SystemDrive\Users", $env:APPDATA, $env:LOCALAPPDATA,
    "$env:USERPROFILE\Documents", "$env:USERPROFILE\Desktop", "$env:USERPROFILE\Downloads"
) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\').ToLowerInvariant() }

function Test-SafeTarget {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $norm = $Path.TrimEnd('\').ToLowerInvariant()
    # An undefined environment variable collapses a target to something like
    # "\lib-bad", which would resolve against the current drive. Require an
    # absolute local path with at least one component below the root, so a
    # bare "C:\" can never be a target.
    if ($norm -notmatch '^[a-z]:\\[^\\]') { return $false }
    if ($script:ProtectedPaths -contains $norm) { return $false }
    return $true
}

function Expand-TargetPath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return @() }
    if ($Path -notmatch '\*') { return @($Path) }
    try {
        return @(Resolve-Path -Path $Path -ErrorAction SilentlyContinue |
                 ForEach-Object { $_.ProviderPath })
    } catch { return @() }
}

function Get-PathSize {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return 0 }
    if (-not (Test-Path -LiteralPath $Path -ErrorAction SilentlyContinue)) { return 0 }
    try {
        $sum = [long]0
        Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue |
            ForEach-Object { $sum += $_.Length }
        return $sum
    } catch { return 0 }
}

# --- Core cleaning primitives -----------------------------------------------

# Deletes the CONTENTS of a folder (the folder itself survives).
function Clear-Folder {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Path,
        [string]$Filter,
        [int]$OlderThanDays = 0,
        [switch]$Recurse
    )

    foreach ($resolved in (Expand-TargetPath $Path)) {
        try {
            if (-not (Test-Path -LiteralPath $resolved -ErrorAction SilentlyContinue)) { continue }
            if (-not (Test-SafeTarget $resolved)) {
                Write-Warning "  [guard] $Name - refusing to clean protected path: $resolved"
                continue
            }
            # Several targets legitimately resolve to the same folder (TEMP and
            # LocalAppData\Temp, for one). Clean each folder once so the totals
            # do not double-count. The filter is part of the key: the same
            # folder is deliberately visited twice with different filters
            # (thumbcache_*.db then iconcache_*.db).
            $key = '{0}|{1}|{2}' -f $resolved.TrimEnd('\'), $Filter, $OlderThanDays
            if (-not $script:SeenPaths.Add($key)) { continue }

            $gci = @{ LiteralPath = $resolved; Force = $true; ErrorAction = 'SilentlyContinue' }
            if ($Filter)  { $gci.Filter = $Filter }
            if ($Recurse) { $gci.Recurse = $true; $gci.File = $true }

            $items = @(Get-ChildItem @gci)
            if ($OlderThanDays -gt 0) {
                $cutoff = (Get-Date).AddDays(-$OlderThanDays)
                $items = @($items | Where-Object { $_.LastWriteTime -lt $cutoff })
            }
            if ($items.Count -eq 0) { continue }

            $sized = foreach ($i in $items) {
                $sz = if ($i.PSIsContainer) { Get-PathSize $i.FullName } else { [long]$i.Length }
                [pscustomobject]@{ Item = $i; Size = $sz }
            }
            $before = [long](($sized | Measure-Object -Property Size -Sum).Sum)

            $freed = [long]0
            if ($DryRun) {
                $freed = $before
            } else {
                foreach ($s in $sized) {
                    try {
                        Remove-Item -LiteralPath $s.Item.FullName -Force -Recurse -ErrorAction Stop
                        $freed += $s.Size
                    } catch {
                        # A folder that only partly deleted still freed something.
                        if ($s.Item.PSIsContainer) { $freed += ($s.Size - (Get-PathSize $s.Item.FullName)) }
                        $script:LockedCount++
                    }
                }
            }

            if ($freed -gt 0) {
                Write-Detail ('  [clean]  {0,-44} {1}' -f $Name, (Format-Bytes $freed))
                Add-Result -Name $Name -Freed $freed
            } elseif ($before -gt 0) {
                Write-Detail ('  [locked] {0,-44} {1} in use' -f $Name, (Format-Bytes $before))
            }
        } catch {
            $script:ErrorCount++
            Write-Warning "  [error] $Name : $($_.Exception.Message)"
        }
    }
}

# Deletes a path outright (the folder and all of it, or a single file).
function Remove-Target {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Path,
        [switch]$TakeOwnership
    )

    foreach ($resolved in (Expand-TargetPath $Path)) {
        try {
            if (-not (Test-Path -LiteralPath $resolved -ErrorAction SilentlyContinue)) { continue }
            if (-not (Test-SafeTarget $resolved)) {
                Write-Warning "  [guard] $Name - refusing to remove protected path: $resolved"
                continue
            }
            if (-not $script:SeenPaths.Add($resolved.TrimEnd('\'))) { continue }

            $item = Get-Item -LiteralPath $resolved -Force -ErrorAction SilentlyContinue
            if ($null -eq $item) {
                # Present but unreadable (restrictive ACL). Size it as a folder;
                # takeown below is what makes the delete itself possible.
                $before = Get-PathSize $resolved
            } elseif ($item.PSIsContainer) {
                $before = Get-PathSize $resolved
            } else {
                $before = [long]$item.Length
            }

            $freed = $before
            if (-not $DryRun) {
                if ($TakeOwnership) {
                    # Windows.old and $WinREAgent carry ACLs that lock out even
                    # local Administrators until ownership is taken.
                    & takeown.exe /F $resolved /A /R /D Y 2>&1 | Out-Null
                    & icacls.exe  $resolved /grant '*S-1-5-32-544:(OI)(CI)F' /T /C 2>&1 | Out-Null
                }
                try { Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction Stop }
                catch { $script:LockedCount++ }
                $after = if (Test-Path -LiteralPath $resolved) { Get-PathSize $resolved } else { 0 }
                $freed = $before - $after
            }

            Write-Detail ('  [remove] {0,-44} {1}' -f $Name, (Format-Bytes $freed))
            Add-Result -Name $Name -Freed $freed
        } catch {
            $script:ErrorCount++
            Write-Warning "  [error] $Name : $($_.Exception.Message)"
        }
    }
}

function Invoke-Targets {
    param([object[]]$Targets)
    foreach ($t in $Targets) {
        $p = @{ Name = $t.Name; Path = $t.Path }
        if ($t.ContainsKey('Filter'))        { $p.Filter        = $t.Filter }
        if ($t.ContainsKey('OlderThanDays')) { $p.OlderThanDays = $t.OlderThanDays }
        if ($t.ContainsKey('Recurse'))       { $p.Recurse       = [switch]$t.Recurse }
        Clear-Folder @p
    }
}

# For native tools (DISM, cleanmgr, vssadmin, compact) whose reclaimed space
# cannot be measured by enumerating a folder: diff the volume's free space.
# Slightly noisy if something else writes to the disk meanwhile, but it is the
# only honest measure available for these.
function Invoke-NativeStep {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action,
        [string]$WouldRun
    )
    if ($DryRun) {
        $what = if ($WouldRun) { $WouldRun } else { $Name }
        Write-Detail "  [would run] $what"
        return
    }
    Write-Detail "  [run] $Name (this can take a while)"
    $before = Get-FreeSpace
    try { & $Action | Out-Null }
    catch {
        $script:ErrorCount++
        Write-Warning "  [error] $Name : $($_.Exception.Message)"
        return
    }
    $delta = (Get-FreeSpace) - $before
    if ($delta -lt 0) { $delta = 0 }
    Write-Detail ('  [done]   {0,-44} {1}' -f $Name, (Format-Bytes $delta))
    Add-Result -Name $Name -Freed $delta
}

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)
    try { & $Action }
    catch {
        $script:ErrorCount++
        Write-Warning "  [error] $Name : $($_.Exception.Message)"
    }
}

function Get-UserProfilePath {
    $paths = @()
    try {
        Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList' -ErrorAction SilentlyContinue |
            ForEach-Object {
                $p = (Get-ItemProperty -Path $_.PSPath -ErrorAction SilentlyContinue).ProfileImagePath
                if ($p -and $p -like "$env:SystemDrive\Users\*" -and (Test-Path -LiteralPath $p)) { $paths += $p }
            }
    } catch { }
    if ($paths.Count -eq 0) {
        $paths = @(Get-ChildItem "$env:SystemDrive\Users" -Directory -ErrorAction SilentlyContinue |
                   ForEach-Object { $_.FullName })
    }
    return @($paths | Sort-Object -Unique)
}

# ============================================================================
#  Preflight
# ============================================================================

$isAdmin = Test-IsAdmin

Write-Host ''
Write-Host '=== Windows Deep Disk Cleaner ===' -ForegroundColor Green
if ($DryRun) { Write-Host 'DRY RUN - nothing will be deleted' -ForegroundColor Yellow }
if (-not $isAdmin) {
    Write-Warning 'Not elevated. System-level items (Windows Update cache, Prefetch, WinSxS, hibernation, shadow copies, other user profiles) will be skipped.'
}

# One combined confirmation covering everything irreversible.
$risky = @()
if ($IncludeWindowsOld)                        { $risky += 'delete Windows.old (no rollback to the previous Windows build)' }
if ($IncludeShadowCopies)                      { $risky += 'delete System Restore points / shadow copies' }
if ($IncludeEventLogs)                         { $risky += 'clear all Windows event logs' }
if ($Aggressive -and $IncludeComponentCleanup) { $risky += 'DISM /ResetBase (installed updates can no longer be uninstalled)' }
if ($Aggressive -and $IncludeDevCaches)        { $risky += 'wipe package stores (.nuget, .m2, go modules) and prune Docker' }
if ($Hibernation -eq 'Off')                    { $risky += 'disable hibernation and Fast Startup' }

if ($risky.Count -gt 0 -and -not $DryRun -and -not $Yes) {
    Write-Host ''
    Write-Host 'The following steps are irreversible:' -ForegroundColor Yellow
    $risky | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
    if ([Environment]::UserInteractive) {
        $answer = Read-Host 'Continue? [y/N]'
        if ($answer -notmatch '^[Yy]') { Write-Host 'Aborted.' -ForegroundColor Red; return }
    } else {
        Write-Warning 'Non-interactive session and -Yes was not supplied. Aborting.'
        return
    }
}

$freeBefore = Get-FreeSpace
Write-Note ('Free space on {0} before: {1}' -f $env:SystemDrive, (Format-Bytes $freeBefore))

# ============================================================================
#  1. User temp and caches
# ============================================================================

Write-Info "`n-- User temp and caches --"
Invoke-Targets @(
    @{ Name = 'User temp';                       Path = $env:TEMP }
    @{ Name = 'User temp (LocalAppData)';        Path = "$env:LOCALAPPDATA\Temp" }
    @{ Name = 'INetCache';                       Path = "$env:LOCALAPPDATA\Microsoft\Windows\INetCache" }
    @{ Name = 'WebCache';                        Path = "$env:LOCALAPPDATA\Microsoft\Windows\WebCache" }
    @{ Name = 'Windows Caches';                  Path = "$env:LOCALAPPDATA\Microsoft\Windows\Caches" }
    @{ Name = 'CryptnetUrlCache (user)';         Path = "$env:LOCALAPPDATA\Microsoft\CryptnetUrlCache" }
    @{ Name = 'Thumbnail cache';                 Path = "$env:LOCALAPPDATA\Microsoft\Windows\Explorer"; Filter = 'thumbcache_*.db' }
    @{ Name = 'Icon cache';                      Path = "$env:LOCALAPPDATA\Microsoft\Windows\Explorer"; Filter = 'iconcache_*.db' }
    @{ Name = 'Icon cache (legacy)';             Path = "$env:LOCALAPPDATA\Microsoft\Windows"; Filter = 'IconCache.db' }
    @{ Name = 'Windows Error Reporting (user)';  Path = "$env:LOCALAPPDATA\Microsoft\Windows\WER" }
    @{ Name = 'Crash dumps (user)';              Path = "$env:LOCALAPPDATA\CrashDumps" }
    @{ Name = 'D3D shader cache';                Path = "$env:LOCALAPPDATA\D3DSCache" }
    @{ Name = 'NVIDIA DX shader cache';          Path = "$env:LOCALAPPDATA\NVIDIA\DXCache" }
    @{ Name = 'NVIDIA GL shader cache';          Path = "$env:LOCALAPPDATA\NVIDIA\GLCache" }
    @{ Name = 'NVIDIA driver cache';             Path = "$env:ProgramData\NVIDIA Corporation\NV_Cache" }
    @{ Name = 'AMD shader cache';                Path = "$env:LOCALAPPDATA\AMD\DxCache" }
    @{ Name = 'AMD shader cache (DXC)';          Path = "$env:LOCALAPPDATA\AMD\DxcCache" }
    @{ Name = 'Intel shader cache';              Path = "$env:LOCALAPPDATA\Intel\ShaderCache" }
    @{ Name = 'Remote Desktop bitmap cache';     Path = "$env:LOCALAPPDATA\Microsoft\Terminal Server Client\Cache" }
    @{ Name = 'UWP app INetCache';               Path = "$env:LOCALAPPDATA\Packages\*\AC\INetCache" }
    @{ Name = 'UWP app temp';                    Path = "$env:LOCALAPPDATA\Packages\*\AC\Temp" }
    @{ Name = 'UWP app TempState';               Path = "$env:LOCALAPPDATA\Packages\*\TempState" }
    @{ Name = 'LocalLow temp';                   Path = "$env:USERPROFILE\AppData\LocalLow\Temp" }
)

# ============================================================================
#  2. Browser caches (opt-in)
# ============================================================================

if ($IncludeBrowserCache) {
    Write-Info "`n-- Browser caches --"

    # Every Chromium-family browser shares the same profile layout, so one
    # table of roots times one table of cache subfolders covers all of them.
    $chromiumRoots = @(
        @{ Name = 'Chrome';        Path = "$env:LOCALAPPDATA\Google\Chrome\User Data" }
        @{ Name = 'Chrome Beta';   Path = "$env:LOCALAPPDATA\Google\Chrome Beta\User Data" }
        @{ Name = 'Chrome Canary'; Path = "$env:LOCALAPPDATA\Google\Chrome SxS\User Data" }
        @{ Name = 'Edge';          Path = "$env:LOCALAPPDATA\Microsoft\Edge\User Data" }
        @{ Name = 'Edge Beta';     Path = "$env:LOCALAPPDATA\Microsoft\Edge Beta\User Data" }
        @{ Name = 'Brave';         Path = "$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\User Data" }
        @{ Name = 'Vivaldi';       Path = "$env:LOCALAPPDATA\Vivaldi\User Data" }
        @{ Name = 'Chromium';      Path = "$env:LOCALAPPDATA\Chromium\User Data" }
        @{ Name = 'Yandex';        Path = "$env:LOCALAPPDATA\Yandex\YandexBrowser\User Data" }
        @{ Name = 'Opera';         Path = "$env:APPDATA\Opera Software\Opera Stable" }
        @{ Name = 'Opera GX';      Path = "$env:APPDATA\Opera Software\Opera GX Stable" }
    )
    $chromiumCacheDirs = @(
        'Cache', 'Code Cache', 'GPUCache', 'ShaderCache', 'GrShaderCache',
        'DawnCache', 'DawnGraphiteCache', 'DawnWebGPUCache', 'GraphiteDawnCache',
        'Service Worker\CacheStorage', 'Service Worker\ScriptCache',
        'Storage\ext\*\def\Cache'
    )

    foreach ($root in $chromiumRoots) {
        if (-not (Test-Path -LiteralPath $root.Path)) { continue }
        # The User Data root holds shared caches; each profile holds its own.
        $profileDirs = @($root.Path) + @(
            Get-ChildItem -LiteralPath $root.Path -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq 'Default' -or $_.Name -like 'Profile*' -or $_.Name -like '*Profile' } |
                ForEach-Object { $_.FullName }
        )
        foreach ($prof in $profileDirs) {
            foreach ($dir in $chromiumCacheDirs) {
                Clear-Folder -Name "$($root.Name) cache" -Path (Join-Path $prof $dir)
            }
        }
    }

    # Firefox keeps its real cache under LocalAppData, not AppData.
    Invoke-Targets @(
        @{ Name = 'Firefox cache';          Path = "$env:LOCALAPPDATA\Mozilla\Firefox\Profiles\*\cache2" }
        @{ Name = 'Firefox startup cache';  Path = "$env:LOCALAPPDATA\Mozilla\Firefox\Profiles\*\startupCache" }
        @{ Name = 'Firefox thumbnails';     Path = "$env:LOCALAPPDATA\Mozilla\Firefox\Profiles\*\thumbnails" }
        @{ Name = 'Firefox jumplist cache'; Path = "$env:LOCALAPPDATA\Mozilla\Firefox\Profiles\*\jumpListCache" }
        @{ Name = 'Firefox updates';        Path = "$env:LOCALAPPDATA\Mozilla\updates" }
        @{ Name = 'Firefox crash reports';  Path = "$env:APPDATA\Mozilla\Firefox\Crash Reports" }
    )
} else {
    Write-Note "`n(browser caches skipped - use -IncludeBrowserCache)"
}

# ============================================================================
#  3. Application caches (opt-in)
# ============================================================================

if ($IncludeAppCaches) {
    Write-Info "`n-- Application caches --"
    Invoke-Targets @(
        @{ Name = 'Teams (classic) cache';      Path = "$env:APPDATA\Microsoft\Teams\Cache" }
        @{ Name = 'Teams (classic) code cache'; Path = "$env:APPDATA\Microsoft\Teams\Code Cache" }
        @{ Name = 'Teams (classic) GPU cache';  Path = "$env:APPDATA\Microsoft\Teams\GPUCache" }
        @{ Name = 'Teams (classic) blobs';      Path = "$env:APPDATA\Microsoft\Teams\blob_storage" }
        @{ Name = 'Teams (classic) tmp';        Path = "$env:APPDATA\Microsoft\Teams\tmp" }
        @{ Name = 'Teams (new) webview cache';  Path = "$env:LOCALAPPDATA\Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\Default\Cache" }
        @{ Name = 'Discord cache';              Path = "$env:APPDATA\discord\Cache" }
        @{ Name = 'Discord code cache';         Path = "$env:APPDATA\discord\Code Cache" }
        @{ Name = 'Discord GPU cache';          Path = "$env:APPDATA\discord\GPUCache" }
        @{ Name = 'Slack cache';                Path = "$env:APPDATA\Slack\Cache" }
        @{ Name = 'Slack service worker';       Path = "$env:APPDATA\Slack\Service Worker\CacheStorage" }
        @{ Name = 'Spotify storage';            Path = "$env:LOCALAPPDATA\Spotify\Storage" }
        @{ Name = 'Spotify data';               Path = "$env:LOCALAPPDATA\Spotify\Data" }
        @{ Name = 'Steam http cache';           Path = "${env:ProgramFiles(x86)}\Steam\appcache\httpcache" }
        @{ Name = 'Steam logs';                 Path = "${env:ProgramFiles(x86)}\Steam\logs" }
        @{ Name = 'Steam crash dumps';          Path = "${env:ProgramFiles(x86)}\Steam\dumps" }
        @{ Name = 'Steam shader cache';         Path = "${env:ProgramFiles(x86)}\Steam\steamapps\shadercache" }
        @{ Name = 'Epic web cache';             Path = "$env:LOCALAPPDATA\EpicGamesLauncher\Saved\webcache*" }
        @{ Name = 'Epic logs';                  Path = "$env:LOCALAPPDATA\EpicGamesLauncher\Saved\Logs" }
        @{ Name = 'Battle.net cache';           Path = "$env:APPDATA\Battle.net\Cache" }
        @{ Name = 'Zoom logs';                  Path = "$env:APPDATA\Zoom\logs" }
        @{ Name = 'OneDrive logs';              Path = "$env:LOCALAPPDATA\Microsoft\OneDrive\logs" }
        @{ Name = 'OneDrive setup logs';        Path = "$env:LOCALAPPDATA\Microsoft\OneDrive\setup\logs" }
        @{ Name = 'Adobe media cache files';    Path = "$env:APPDATA\Adobe\Common\Media Cache Files" }
        @{ Name = 'Adobe media cache db';       Path = "$env:APPDATA\Adobe\Common\Media Cache" }
        @{ Name = 'Microsoft Store cache';      Path = "$env:LOCALAPPDATA\Packages\Microsoft.WindowsStore_8wekyb3d8bbwe\LocalCache" }
        @{ Name = 'Electron app caches';        Path = "$env:APPDATA\*\Cache\Cache_Data" }
    )
    if ($Aggressive) {
        # Holds not-yet-uploaded edits to Office documents, so only with -Aggressive.
        Clear-Folder -Name 'Office upload cache' -Path "$env:LOCALAPPDATA\Microsoft\Office\*\OfficeFileCache"
    }
} else {
    Write-Note "`n(application caches skipped - use -IncludeAppCaches)"
}

# ============================================================================
#  4. Developer tool caches (opt-in)
# ============================================================================

if ($IncludeDevCaches) {
    Write-Info "`n-- Developer tool caches --"
    Invoke-Targets @(
        @{ Name = 'npm cache';               Path = "$env:LOCALAPPDATA\npm-cache" }
        @{ Name = 'npm cache (roaming)';     Path = "$env:APPDATA\npm-cache" }
        @{ Name = 'yarn cache';              Path = "$env:LOCALAPPDATA\Yarn\Cache" }
        @{ Name = 'pnpm cache';              Path = "$env:LOCALAPPDATA\pnpm-cache" }
        @{ Name = 'pip cache';               Path = "$env:LOCALAPPDATA\pip\Cache" }
        @{ Name = 'NuGet http cache';        Path = "$env:LOCALAPPDATA\NuGet\v3-cache" }
        @{ Name = 'NuGet plugins cache';     Path = "$env:LOCALAPPDATA\NuGet\plugins-cache" }
        @{ Name = 'Gradle caches';           Path = "$env:USERPROFILE\.gradle\caches" }
        @{ Name = 'Gradle daemon logs';      Path = "$env:USERPROFILE\.gradle\daemon" }
        @{ Name = 'Cargo registry cache';    Path = "$env:USERPROFILE\.cargo\registry\cache" }
        @{ Name = 'Cargo registry src';      Path = "$env:USERPROFILE\.cargo\registry\src" }
        @{ Name = 'Cargo git checkouts';     Path = "$env:USERPROFILE\.cargo\git\checkouts" }
        @{ Name = 'Go build cache';          Path = "$env:LOCALAPPDATA\go-build" }
        @{ Name = 'Composer cache';          Path = "$env:LOCALAPPDATA\Composer" }
        @{ Name = 'vcpkg binary cache';      Path = "$env:LOCALAPPDATA\vcpkg\archives" }
        @{ Name = 'Chocolatey bad packages'; Path = "$env:ChocolateyInstall\lib-bad" }
        @{ Name = 'conda package cache';     Path = "$env:USERPROFILE\.conda\pkgs" }
        @{ Name = 'VS Code cache';           Path = "$env:APPDATA\Code\Cache" }
        @{ Name = 'VS Code cached data';     Path = "$env:APPDATA\Code\CachedData" }
        @{ Name = 'VS Code code cache';      Path = "$env:APPDATA\Code\Code Cache" }
        @{ Name = 'VS Code GPU cache';       Path = "$env:APPDATA\Code\GPUCache" }
        @{ Name = 'VS Code logs';            Path = "$env:APPDATA\Code\logs" }
        @{ Name = 'VS Code extension VSIXs'; Path = "$env:APPDATA\Code\CachedExtensionVSIXs" }
        @{ Name = 'Visual Studio MEF cache'; Path = "$env:LOCALAPPDATA\Microsoft\VisualStudio\*\ComponentModelCache" }
        @{ Name = 'Visual Studio telemetry'; Path = "$env:LOCALAPPDATA\Microsoft\VSApplicationInsights" }
        @{ Name = 'JetBrains caches';        Path = "$env:LOCALAPPDATA\JetBrains\*\caches" }
        @{ Name = 'JetBrains logs';          Path = "$env:LOCALAPPDATA\JetBrains\*\log" }
        @{ Name = 'Unity cache';             Path = "$env:LOCALAPPDATA\Unity\cache" }
        @{ Name = 'Unreal derived data';     Path = "$env:LOCALAPPDATA\UnrealEngine\Common\DerivedDataCache" }
        @{ Name = 'Android SDK temp';        Path = "$env:LOCALAPPDATA\Android\Sdk\.temp" }
        @{ Name = 'Android build cache';     Path = "$env:USERPROFILE\.android\cache" }
    )

    if ($Aggressive) {
        # These are real package stores, not caches: emptying them means every
        # project re-downloads its dependencies on the next build.
        Invoke-Targets @(
            @{ Name = 'NuGet global packages'; Path = "$env:USERPROFILE\.nuget\packages" }
            @{ Name = 'Maven repository';      Path = "$env:USERPROFILE\.m2\repository" }
            @{ Name = 'Go module cache';       Path = "$env:USERPROFILE\go\pkg\mod" }
        )
        if (Get-Command docker -ErrorAction SilentlyContinue) {
            Invoke-NativeStep -Name 'Docker prune' -WouldRun 'docker system prune -af --volumes' -Action {
                & docker system prune -af --volumes 2>&1 | Out-Null
            }
        }
    }
} else {
    Write-Note "`n(developer caches skipped - use -IncludeDevCaches)"
}

# ============================================================================
#  5. Privacy artifacts (opt-in)
# ============================================================================

if ($IncludePrivacy) {
    Write-Info "`n-- Recent items, jump lists, history --"
    Invoke-Targets @(
        @{ Name = 'Jump lists (automatic)';  Path = "$env:APPDATA\Microsoft\Windows\Recent\AutomaticDestinations" }
        @{ Name = 'Jump lists (custom)';     Path = "$env:APPDATA\Microsoft\Windows\Recent\CustomDestinations" }
        @{ Name = 'Recent items';            Path = "$env:APPDATA\Microsoft\Windows\Recent" }
        @{ Name = 'Browsing history index';  Path = "$env:LOCALAPPDATA\Microsoft\Windows\History" }
        @{ Name = 'INetCookies';             Path = "$env:LOCALAPPDATA\Microsoft\Windows\INetCookies" }
    )
} else {
    Write-Note "`n(recent items / jump lists skipped - use -IncludePrivacy)"
}

# ============================================================================
#  6. System temp, logs and dumps (admin)
# ============================================================================

if ($isAdmin) {
    Write-Info "`n-- System temp and caches --"
    Invoke-Targets @(
        @{ Name = 'Windows Temp';                    Path = "$env:WINDIR\Temp" }
        @{ Name = 'C:\Temp';                         Path = "$env:SystemDrive\Temp" }
        @{ Name = 'Prefetch';                        Path = "$env:WINDIR\Prefetch" }
        @{ Name = 'PerfLogs';                        Path = "$env:SystemDrive\PerfLogs" }
        @{ Name = 'Downloaded Program Files';        Path = "$env:WINDIR\Downloaded Program Files" }
        @{ Name = 'Offline Web Pages';               Path = "$env:WINDIR\Offline Web Pages" }
        @{ Name = 'System profile INetCache';        Path = "$env:WINDIR\System32\config\systemprofile\AppData\Local\Microsoft\Windows\INetCache" }
        @{ Name = 'System profile INetCache (x86)';  Path = "$env:WINDIR\SysWOW64\config\systemprofile\AppData\Local\Microsoft\Windows\INetCache" }
        @{ Name = 'System CryptnetUrlCache';         Path = "$env:WINDIR\System32\config\systemprofile\AppData\LocalLow\Microsoft\CryptnetUrlCache" }
        @{ Name = 'ASP.NET temporary files';         Path = "$env:WINDIR\Microsoft.NET\Framework*\v*\Temporary ASP.NET Files" }
        @{ Name = 'Font cache (service)';            Path = "$env:WINDIR\ServiceProfiles\LocalService\AppData\Local\FontCache" }
    )

    Write-Info "`n-- Logs, dumps and error reports --"
    Invoke-Targets @(
        @{ Name = 'CBS logs';              Path = "$env:WINDIR\Logs\CBS" }
        @{ Name = 'DISM logs';             Path = "$env:WINDIR\Logs\DISM" }
        @{ Name = 'Windows Update logs';   Path = "$env:WINDIR\Logs\WindowsUpdate" }
        @{ Name = 'MoSetup logs';          Path = "$env:WINDIR\Logs\MoSetup" }
        @{ Name = 'SIH logs';              Path = "$env:WINDIR\Logs\SIH" }
        @{ Name = 'waasmedic logs';        Path = "$env:WINDIR\Logs\waasmedic" }
        @{ Name = 'Windows logs (7d+)';    Path = "$env:WINDIR\Logs"; Filter = '*.log'; Recurse = $true; OlderThanDays = 7 }
        @{ Name = 'Setup logs (Panther)';  Path = "$env:WINDIR\Panther" }
        @{ Name = 'setupapi logs';         Path = "$env:WINDIR\inf"; Filter = 'setupapi.*.log' }
        @{ Name = 'Debug logs';            Path = "$env:WINDIR\debug"; Filter = '*.log' }
        @{ Name = 'Security logs';         Path = "$env:WINDIR\security\logs"; Filter = '*.log' }
        @{ Name = 'System32 LogFiles (7d+)'; Path = "$env:WINDIR\System32\LogFiles"; Filter = '*.log'; Recurse = $true; OlderThanDays = 7 }
        @{ Name = 'Minidumps';             Path = "$env:WINDIR\Minidump" }
        @{ Name = 'Live kernel reports';   Path = "$env:WINDIR\LiveKernelReports" }
        @{ Name = 'WER queue';             Path = "$env:ProgramData\Microsoft\Windows\WER\ReportQueue" }
        @{ Name = 'WER archive';           Path = "$env:ProgramData\Microsoft\Windows\WER\ReportArchive" }
        @{ Name = 'WER temp';              Path = "$env:ProgramData\Microsoft\Windows\WER\Temp" }
        @{ Name = 'Diagnostics ETL logs';  Path = "$env:ProgramData\Microsoft\Diagnosis"; Filter = '*.etl'; Recurse = $true }
        @{ Name = 'Diagnostics RBS';       Path = "$env:ProgramData\Microsoft\Diagnosis"; Filter = '*.rbs'; Recurse = $true }
        @{ Name = 'Defender scan history'; Path = "$env:ProgramData\Microsoft\Windows Defender\Scans\History\Results" }
    )
    Invoke-Step 'MEMORY.DMP' { Remove-Target -Name 'Kernel memory dump (MEMORY.DMP)' -Path "$env:WINDIR\MEMORY.DMP" }

    # ------------------------------------------------------------------
    #  Windows Update / Delivery Optimization
    # ------------------------------------------------------------------

    Write-Info "`n-- Windows Update and Delivery Optimization --"
    Invoke-Step 'Windows Update cache' {
        $services = @('wuauserv', 'bits', 'dosvc')
        $stopped  = @()
        if (-not $DryRun) {
            foreach ($s in $services) {
                $svc = Get-Service -Name $s -ErrorAction SilentlyContinue
                if ($svc -and $svc.Status -eq 'Running') {
                    Stop-Service -Name $s -Force -ErrorAction SilentlyContinue
                    $stopped += $s
                }
            }
        }

        Invoke-Targets @(
            @{ Name = 'Windows Update downloads';    Path = "$env:WINDIR\SoftwareDistribution\Download" }
            @{ Name = 'Delivery Optimization cache'; Path = "$env:WINDIR\SoftwareDistribution\DeliveryOptimization" }
            @{ Name = 'Delivery Optimization logs';  Path = "$env:WINDIR\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Logs" }
        )
        if ($Aggressive) {
            Invoke-Targets @(
                @{ Name = 'Windows Update datastore';   Path = "$env:WINDIR\SoftwareDistribution\DataStore" }
                @{ Name = 'catroot2 (signature cache)'; Path = "$env:WINDIR\System32\catroot2" }
                @{ Name = 'BITS transfer queue';        Path = "$env:ProgramData\Microsoft\Network\Downloader"; Filter = 'qmgr*.dat' }
            )
        }

        if (-not $DryRun) {
            foreach ($s in $stopped) { Start-Service -Name $s -ErrorAction SilentlyContinue }
        }
    }

    if (Get-Command Delete-DeliveryOptimizationCache -ErrorAction SilentlyContinue) {
        Invoke-NativeStep -Name 'Delivery Optimization (cmdlet)' -WouldRun 'Delete-DeliveryOptimizationCache -Force' -Action {
            Delete-DeliveryOptimizationCache -Force -ErrorAction SilentlyContinue
        }
    }

    # Superseded virus definition deltas pile up and are never pruned on their own.
    Invoke-Step 'Defender definitions' {
        $mp = Get-ChildItem "$env:ProgramData\Microsoft\Windows Defender\Platform" -Directory -ErrorAction SilentlyContinue |
              Sort-Object Name -Descending | Select-Object -First 1
        $exe = if ($mp) { Join-Path $mp.FullName 'MpCmdRun.exe' } else { "$env:ProgramFiles\Windows Defender\MpCmdRun.exe" }
        if (Test-Path -LiteralPath $exe) {
            Invoke-NativeStep -Name 'Defender old definitions' -WouldRun 'MpCmdRun.exe -RemoveDefinitions -DynamicSignatures' -Action {
                & $exe -RemoveDefinitions -DynamicSignatures 2>&1 | Out-Null
            }
        }
    }

    if ($Aggressive) {
        Write-Info "`n-- Installer caches (aggressive) --"
        Invoke-Targets @(
            @{ Name = 'MSI patch cache';     Path = "$env:WINDIR\Installer\`$PatchCache`$\Managed" }
            @{ Name = 'Config.Msi rollback'; Path = "$env:SystemDrive\Config.Msi" }
        )
    }

    # ------------------------------------------------------------------
    #  Other user profiles
    # ------------------------------------------------------------------

    Write-Info "`n-- Other user profiles --"
    foreach ($profilePath in (Get-UserProfilePath)) {
        if ($profilePath.TrimEnd('\') -eq $env:USERPROFILE.TrimEnd('\')) { continue }
        $who = Split-Path $profilePath -Leaf
        if ($who -in @('Public', 'Default', 'Default User', 'All Users')) { continue }
        Invoke-Targets @(
            @{ Name = "Temp ($who)";       Path = "$profilePath\AppData\Local\Temp" }
            @{ Name = "INetCache ($who)";  Path = "$profilePath\AppData\Local\Microsoft\Windows\INetCache" }
            @{ Name = "WER ($who)";        Path = "$profilePath\AppData\Local\Microsoft\Windows\WER" }
            @{ Name = "CrashDumps ($who)"; Path = "$profilePath\AppData\Local\CrashDumps" }
            @{ Name = "Thumbnails ($who)"; Path = "$profilePath\AppData\Local\Microsoft\Windows\Explorer"; Filter = 'thumbcache_*.db' }
        )
    }
} else {
    Write-Note "`n(system-level items skipped - re-run elevated for full cleanup)"
}

# ============================================================================
#  7. Recycle Bin (all drives)
# ============================================================================

Write-Info "`n-- Recycle Bin --"
Invoke-Step 'Recycle Bin' {
    $measure = {
        $total = [long]0
        Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue |
            Where-Object { $_.Root -match '^[A-Za-z]:\\$' -and -not $_.DisplayRoot } |
            ForEach-Object { $total += Get-PathSize (Join-Path $_.Root '$Recycle.Bin') }
        $total
    }

    $before = & $measure
    if ($before -le 0) {
        Write-Detail '  [skip]   Recycle Bin - empty'
        return
    }

    if ($DryRun) {
        Write-Detail ('  [clean]  {0,-44} {1}' -f 'Recycle Bin', (Format-Bytes $before))
        Add-Result -Name 'Recycle Bin' -Freed $before
        return
    }

    Clear-RecycleBin -Force -ErrorAction SilentlyContinue
    $freed = $before - (& $measure)
    Write-Detail ('  [clean]  {0,-44} {1}' -f 'Recycle Bin', (Format-Bytes $freed))
    Add-Result -Name 'Recycle Bin' -Freed $freed
}

# ============================================================================
#  8. Windows upgrade leftovers and Windows.old
# ============================================================================

if ($isAdmin) {
    Write-Info "`n-- Windows upgrade leftovers --"
    # Always safe: aborted or completed setup scratch space, no rollback value.
    foreach ($leftover in @('$GetCurrent', '$SysReset', '$WinREAgent', 'ESD', 'Windows10Upgrade')) {
        $leftoverPath = Join-Path "$env:SystemDrive\" $leftover
        Invoke-Step $leftover {
            Remove-Target -Name "Upgrade leftover ($leftover)" -Path $leftoverPath -TakeOwnership
        }
    }

    if ($IncludeWindowsOld) {
        Write-Info "`n-- Windows.old and rollback data (irreversible) --"
        foreach ($rollback in @('Windows.old', '$Windows.~BT', '$Windows.~WS', '$INPLACE.~TR', '$WINDOWS.~Q')) {
            $rollbackPath = Join-Path "$env:SystemDrive\" $rollback
            Invoke-Step $rollback {
                Remove-Target -Name "Rollback ($rollback)" -Path $rollbackPath -TakeOwnership
            }
        }
    } else {
        Write-Note '  (Windows.old kept - use -IncludeWindowsOld to remove it)'
    }
}

# ============================================================================
#  9. Hibernation
# ============================================================================

Write-Info "`n-- Hibernation --"
Invoke-Step 'Hibernation' {
    $hiberSize = Get-RootFileSize 'hiberfil.sys'

    # Registry rather than "powercfg /a", whose output is localised.
    $hiberEnabled = $false
    try {
        $reg = Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\Power' -ErrorAction SilentlyContinue
        $hiberEnabled = ($reg.HibernateEnabled -eq 1)
    } catch { }

    if ($hiberSize -gt 0) {
        Write-Detail ('  hiberfil.sys is currently {0}' -f (Format-Bytes $hiberSize))
    } else {
        Write-Detail '  hibernation file not present - nothing to reclaim'
    }

    if ($Hibernation -eq 'Keep') {
        if ($hiberSize -gt 0) {
            Write-Note '  (use -Hibernation Reduce to shrink it, or -Hibernation Off to remove it)'
        }
        return
    }
    if (-not $isAdmin) {
        Write-Warning '  Changing hibernation requires an elevated session - skipped.'
        return
    }
    if ($hiberSize -eq 0 -and -not $hiberEnabled) { return }

    if ($Hibernation -eq 'Off') {
        if ($DryRun) {
            Write-Detail ('  [would run] powercfg.exe /hibernate off (frees {0})' -f (Format-Bytes $hiberSize))
            Add-Result -Name 'hiberfil.sys (hibernation off)' -Freed $hiberSize
            return
        }
        Write-Detail '  [run] powercfg.exe /hibernate off'
        & powercfg.exe /hibernate off 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        $freed = $hiberSize - (Get-RootFileSize 'hiberfil.sys')
        Write-Detail ('  [done]   {0,-44} {1}' -f 'hibernation disabled', (Format-Bytes $freed))
        Write-Warning '  Fast Startup is now off as well. Re-enable both with: powercfg /hibernate on'
        Add-Result -Name 'hiberfil.sys (hibernation off)' -Freed $freed
    }
    else {
        # Reduced mode keeps Fast Startup working but drops full hibernate.
        if ($DryRun) {
            Write-Detail '  [would run] powercfg.exe /hibernate /type reduced'
            Add-Result -Name 'hiberfil.sys (reduced)' -Freed ([long]($hiberSize * 0.6))
            return
        }
        Write-Detail '  [run] powercfg.exe /hibernate /type reduced'
        & powercfg.exe /hibernate /type reduced 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        $freed = $hiberSize - (Get-RootFileSize 'hiberfil.sys')
        Write-Detail ('  [done]   {0,-44} {1}' -f 'hiberfil.sys reduced', (Format-Bytes $freed))
        Write-Note '  Full hibernate is no longer available; Fast Startup still works.'
        Add-Result -Name 'hiberfil.sys (reduced)' -Freed $freed
    }
}

# ============================================================================
#  10. Shadow copies / System Restore points (opt-in)
# ============================================================================

if ($IncludeShadowCopies -and $isAdmin) {
    Write-Info "`n-- Shadow copies / System Restore points --"
    Invoke-Step 'Shadow copies' {
        $shadows = @()
        try { $shadows = @(Get-CimInstance -ClassName Win32_ShadowCopy -ErrorAction Stop) } catch { }

        if ($shadows.Count -eq 0) {
            Write-Detail '  [skip]   no shadow copies present'
            return
        }

        # Keep the newest per volume so one restore point survives, unless
        # -Aggressive says take everything.
        $doomed = if ($Aggressive) {
            $shadows
        } else {
            $shadows | Group-Object VolumeName | ForEach-Object {
                $_.Group | Sort-Object InstallDate -Descending | Select-Object -Skip 1
            }
        }
        $doomed = @($doomed)

        Write-Detail ('  {0} shadow copy/copies present, {1} to delete' -f $shadows.Count, $doomed.Count)
        if ($doomed.Count -eq 0) { return }
        if ($DryRun) {
            Write-Detail '  [would run] vssadmin delete shadows /shadow={id} /quiet'
            return
        }

        Invoke-NativeStep -Name 'Shadow copies' -Action {
            foreach ($s in $doomed) {
                & vssadmin.exe delete shadows /shadow=$($s.ID) /quiet 2>&1 | Out-Null
            }
        }
    }
} elseif ($IncludeShadowCopies) {
    Write-Warning 'Shadow copy deletion requires an elevated session - skipped.'
}

# ============================================================================
#  11. Windows Search index (opt-in)
# ============================================================================

if ($IncludeSearchIndex -and $isAdmin) {
    Write-Info "`n-- Windows Search index --"
    Invoke-Step 'Search index' {
        $idx  = "$env:ProgramData\Microsoft\Search\Data\Applications\Windows"
        $size = Get-PathSize $idx
        if ($size -eq 0) { Write-Detail '  [skip]   no index present'; return }

        Write-Detail ('  index is currently {0}' -f (Format-Bytes $size))
        if ($DryRun) {
            Write-Detail ('  [clean]  {0,-44} {1}' -f 'Search index', (Format-Bytes $size))
            Add-Result -Name 'Search index' -Freed $size
            return
        }

        $svc        = Get-Service -Name WSearch -ErrorAction SilentlyContinue
        $wasRunning = ($svc -and $svc.Status -eq 'Running')
        if ($wasRunning) { Stop-Service -Name WSearch -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }
        Clear-Folder -Name 'Search index' -Path $idx
        if ($wasRunning) { Start-Service -Name WSearch -ErrorAction SilentlyContinue }
        Write-Note '  The index rebuilds in the background over the next few hours.'
    }
} elseif ($IncludeSearchIndex) {
    Write-Warning 'Search index reset requires an elevated session - skipped.'
}

# ============================================================================
#  12. Event logs (opt-in)
# ============================================================================

if ($IncludeEventLogs -and $isAdmin) {
    Write-Info "`n-- Event logs --"
    Invoke-Step 'Event logs' {
        $logDir = "$env:WINDIR\System32\winevt\Logs"
        $before = Get-PathSize $logDir
        Write-Detail ('  event logs occupy {0}' -f (Format-Bytes $before))

        if ($DryRun) {
            Write-Detail '  [would run] wevtutil cl <each log>'
            Add-Result -Name 'Event logs' -Freed $before
            return
        }

        $names = @(& wevtutil.exe el 2>$null)
        foreach ($n in $names) { & wevtutil.exe cl "$n" 2>&1 | Out-Null }
        $freed = $before - (Get-PathSize $logDir)
        Write-Detail ('  [done]   cleared {0} log(s) - {1}' -f $names.Count, (Format-Bytes $freed))
        Add-Result -Name 'Event logs' -Freed $freed
    }
} elseif ($IncludeEventLogs) {
    Write-Warning 'Clearing event logs requires an elevated session - skipped.'
}

# ============================================================================
#  13. Component store / WinSxS (opt-in)
# ============================================================================

if ($IncludeComponentCleanup -and $isAdmin) {
    Write-Info "`n-- Component store (WinSxS) --"
    if ($Aggressive) {
        Invoke-NativeStep -Name 'DISM component cleanup (ResetBase)' `
            -WouldRun 'Dism.exe /Online /Cleanup-Image /StartComponentCleanup /ResetBase' -Action {
            & Dism.exe /Online /Cleanup-Image /StartComponentCleanup /ResetBase /Quiet 2>&1 | Out-Null
        }
        Invoke-NativeStep -Name 'DISM service pack cleanup' `
            -WouldRun 'Dism.exe /Online /Cleanup-Image /SPSuperseded' -Action {
            & Dism.exe /Online /Cleanup-Image /SPSuperseded /Quiet 2>&1 | Out-Null
        }
    } else {
        Invoke-NativeStep -Name 'DISM component cleanup' `
            -WouldRun 'Dism.exe /Online /Cleanup-Image /StartComponentCleanup' -Action {
            & Dism.exe /Online /Cleanup-Image /StartComponentCleanup /Quiet 2>&1 | Out-Null
        }
    }
} elseif ($IncludeComponentCleanup) {
    Write-Warning 'DISM component cleanup requires an elevated session - skipped.'
}

# ============================================================================
#  14. Built-in Disk Cleanup handlers (opt-in)
# ============================================================================

if ($RunDiskCleanup -and $isAdmin) {
    Write-Info "`n-- cleanmgr.exe (all handlers) --"
    Invoke-Step 'cleanmgr' {
        $vcRoot = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches'
        if (-not (Test-Path $vcRoot)) { Write-Detail '  [skip]   no VolumeCaches handlers found'; return }

        # cleanmgr's Downloads handler deletes real user files. Never enable it.
        $exclude = @('DownloadsFolder')
        if (-not $IncludeWindowsOld) { $exclude += @('Previous Installations', 'Windows Upgrade Log Files') }
        if (-not $Aggressive)        { $exclude += @('Windows ESD installation files') }

        if ($DryRun) {
            Write-Detail "  [would run] cleanmgr /sagerun:99 with all handlers except: $($exclude -join ', ')"
            return
        }

        Get-ChildItem $vcRoot -ErrorAction SilentlyContinue | ForEach-Object {
            $value = if ($exclude -contains $_.PSChildName) { 0 } else { 2 }
            Set-ItemProperty -Path $_.PSPath -Name 'StateFlags0099' -Value $value -Type DWord -Force -ErrorAction SilentlyContinue
        }

        Invoke-NativeStep -Name 'cleanmgr handlers' -Action {
            $proc = Start-Process -FilePath "$env:WINDIR\System32\cleanmgr.exe" `
                                  -ArgumentList '/sagerun:99' -PassThru -WindowStyle Hidden
            if (-not $proc.WaitForExit(20 * 60 * 1000)) {
                try { $proc.Kill() } catch { }
                Write-Warning '  cleanmgr exceeded 20 minutes and was stopped.'
            }
        }
    }
} elseif ($RunDiskCleanup) {
    Write-Warning 'cleanmgr requires an elevated session - skipped.'
}

# ============================================================================
#  15. Compact OS (opt-in)
# ============================================================================

if ($CompactOS -and $isAdmin) {
    Write-Info "`n-- Compact OS (system binary compression) --"
    Write-Note '  Reversible with: compact.exe /CompactOS:never'
    Invoke-NativeStep -Name 'Compact OS' -WouldRun 'compact.exe /CompactOS:always' -Action {
        & compact.exe /CompactOS:always 2>&1 | Out-Null
    }
} elseif ($CompactOS) {
    Write-Warning 'Compact OS requires an elevated session - skipped.'
}

# ============================================================================
#  Summary
# ============================================================================

Write-Host ''
Write-Host '=== Summary ===' -ForegroundColor Green

$grouped = $script:Results |
    Where-Object { $_.Freed -gt 0 } |
    Group-Object Item |
    ForEach-Object {
        [pscustomobject]@{
            Item  = $_.Name
            Freed = [long](($_.Group | Measure-Object -Property Freed -Sum).Sum)
        }
    } | Sort-Object Freed -Descending

if ($grouped) {
    $grouped | ForEach-Object { Write-Host ('  {0,-46} {1,12}' -f $_.Item, (Format-Bytes $_.Freed)) }
} else {
    Write-Host '  Nothing to clean - the system is already tidy.' -ForegroundColor Gray
}

$label = if ($DryRun) { 'Would free' } else { 'Freed' }
Write-Host ''
Write-Host ('{0} total: {1}' -f $label, (Format-Bytes $script:TotalBytesFreed)) -ForegroundColor Green

if (-not $DryRun) {
    $freeAfter = Get-FreeSpace
    Write-Host ('Free space on {0}: {1} -> {2}' -f $env:SystemDrive, (Format-Bytes $freeBefore), (Format-Bytes $freeAfter)) -ForegroundColor Green
}
if ($script:LockedCount -gt 0) {
    Write-Note "$($script:LockedCount) item(s) were locked by a running process and skipped (this is normal)."
}
if ($script:ErrorCount -gt 0) {
    Write-Note "$($script:ErrorCount) step(s) reported an error - see the warnings above."
}

# --- Space this script deliberately does not touch ---------------------------

Write-Host ''
Write-Host '=== Large items left alone (review manually) ===' -ForegroundColor Green

$manual = @()
foreach ($f in @('pagefile.sys', 'swapfile.sys', 'hiberfil.sys')) {
    $sz = Get-RootFileSize $f
    if ($sz -gt 0) { $manual += [pscustomobject]@{ Item = $f; Size = $sz } }
}
$dl = Get-PathSize "$env:USERPROFILE\Downloads"
if ($dl -gt 0) { $manual += [pscustomobject]@{ Item = 'Downloads folder'; Size = $dl } }
try {
    Get-CimInstance -ClassName Win32_ShadowStorage -ErrorAction Stop | ForEach-Object {
        if ($_.UsedSpace -gt 0) {
            $manual += [pscustomobject]@{ Item = 'Shadow copy storage (restore points)'; Size = [long]$_.UsedSpace }
        }
    }
} catch { }

if ($manual.Count -gt 0) {
    $manual | Sort-Object Size -Descending | ForEach-Object {
        Write-Host ('  {0,-46} {1,12}' -f $_.Item, (Format-Bytes $_.Size)) -ForegroundColor Gray
    }
} else {
    Write-Host '  (none)' -ForegroundColor Gray
}

# --- Hints ------------------------------------------------------------------

$hints = @()
if (-not $IncludeBrowserCache)     { $hints += '-IncludeBrowserCache' }
if (-not $IncludeAppCaches)        { $hints += '-IncludeAppCaches' }
if (-not $IncludeDevCaches)        { $hints += '-IncludeDevCaches' }
if (-not $IncludePrivacy)          { $hints += '-IncludePrivacy' }
if (-not $IncludeWindowsOld)       { $hints += '-IncludeWindowsOld' }
if (-not $IncludeComponentCleanup) { $hints += '-IncludeComponentCleanup' }
if (-not $IncludeShadowCopies)     { $hints += '-IncludeShadowCopies' }
if (-not $IncludeSearchIndex)      { $hints += '-IncludeSearchIndex' }
if (-not $IncludeEventLogs)        { $hints += '-IncludeEventLogs' }
if (-not $RunDiskCleanup)          { $hints += '-RunDiskCleanup' }
if ($Hibernation -eq 'Keep' -and (Get-RootFileSize 'hiberfil.sys') -gt 0) { $hints += '-Hibernation Off' }

if ($hints.Count -gt 0) {
    Write-Host ''
    Write-Note ('Not run this time: {0}' -f ($hints -join ' '))
    Write-Note 'Use -All for everything (irreversible), or add -DryRun first to preview.'
}
