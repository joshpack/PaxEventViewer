#Requires -Version 5.1
<#
.SYNOPSIS
    pxd Event Viewer (CLI) - the PC snapshot and event log digest, right in the console.

.DESCRIPTION
    The console edition of pxd Event Viewer. Nothing is installed and nothing on the PC is changed.
    Run it straight from the web:

        irm pxdtek.com/ev | iex

    (pxdtek.com serves the latest copy of
        https://raw.githubusercontent.com/joshpack/pxdEventViewer/main/cli/pxdEventViewer.ps1)

    It shows a PC snapshot (Windows, uptime, pending reboot, hardware, disks, network, antivirus,
    updates) and a digest of the event logs that turns blue screens, unexpected shutdowns, crashes,
    disk errors, failed updates and the rest into one line each, with what to try next. Any log can
    be browsed and filtered. "Copy for ticket" and "Save report" give text or HTML to attach.

    Works as a normal user; only failed sign-ins (the Security log) need an elevated prompt.

    With parameters through the web:
        & ([scriptblock]::Create((irm pxdtek.com/ev))) -Report -Days 30

.PARAMETER Days
    How far back to look: 1, 7 (default) or 30 days (any number from 1 to 365 works).
.PARAMETER Report
    Non-interactive: print the snapshot and the problem digest, then return.
.PARAMETER Export
    Write a report to this path (.html, .txt or .json) and return.
.PARAMETER Ticket
    Copy the ticket text (snapshot + problems) to the clipboard and return.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 365)][int]$Days = 7,
    [switch]$Report,
    [string]$Export,
    [switch]$Ticket
)

$script:PevVersion = '1.1.0'
$script:PevUrl = 'pxdtek.com/ev'
$script:PevMax = 400
# In-memory mode (set by the pxd Toolkit console while it runs this script): nothing is saved on the PC unless the
# user asks for a report (R / -Export). The CLI keeps no settings, logs or caches anyway; this also carries the mode
# over to an elevated window (A).
$script:PevInMemory = $env:PXD_INMEMORY -eq '1'
$script:PevEll = [string][char]0x2026   # built at run time so the file stays ASCII (safe for irm | iex)

# ======================================================================================
#  Helpers
# ======================================================================================

function Get-PevProp($e, [int]$i) { if ($i -lt $e.Props.Count -and $null -ne $e.Props[$i]) { [string]$e.Props[$i] } else { '' } }

function Get-PevTrim([string]$s, [int]$max) { if ($s.Length -le $max) { $s } else { $s.Substring(0, $max - 1) + $script:PevEll } }

function Get-PevFirstLine([string]$s) { ($s -split "`n", 2)[0].Trim() }

function ConvertTo-PevLong([string]$s) {
    $s = "$s".Trim()
    $v = [long]0
    if ($s -match '^0x([0-9a-fA-F]+)$') { [void][long]::TryParse($Matches[1], [Globalization.NumberStyles]::HexNumber, $null, [ref]$v); return $v }
    [void][long]::TryParse($s, [ref]$v); $v
}

function Get-PevAgo([TimeSpan]$t) {
    if ($t.TotalDays -ge 1) { return '{0}d {1}h' -f [int][Math]::Floor($t.TotalDays), $t.Hours }
    if ($t.TotalHours -ge 1) { return '{0}h {1}m' -f [int][Math]::Floor($t.TotalHours), $t.Minutes }
    '{0}m' -f [Math]::Max(0, [int][Math]::Floor($t.TotalMinutes))
}

function Get-PevSize([double]$bytes) {
    if ($bytes -ge 1TB) { return '{0:0.0} TB' -f ($bytes / 1TB) }
    if ($bytes -ge 1GB) { return '{0:0} GB' -f ($bytes / 1GB) }
    if ($bytes -ge 1MB) { return '{0:0} MB' -f ($bytes / 1MB) }
    '{0:0} KB' -f ($bytes / 1KB)
}

function Get-PevDevice([string]$msg) {
    if ($msg -match '\\Device\\(Harddisk\d+|HarddiskVolume\d+|RaidPort\d+)') { return ($Matches[1] -replace 'Harddisk', 'Disk ' -replace 'Disk Volume', 'Volume ') }
    if ($msg -match '(?i)volume ([A-Z]:|\\\\\?\\Volume\{[^}]+\})') { return $Matches[1] }
    'a drive'
}

function Test-PevAdmin {
    try { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } catch { $false }
}

$script:PevStopCodes = @{
    0x0A = 'IRQL_NOT_LESS_OR_EQUAL'; 0x19 = 'BAD_POOL_HEADER'; 0x1A = 'MEMORY_MANAGEMENT'; 0x1E = 'KMODE_EXCEPTION_NOT_HANDLED'
    0x3B = 'SYSTEM_SERVICE_EXCEPTION'; 0x50 = 'PAGE_FAULT_IN_NONPAGED_AREA'; 0x7A = 'KERNEL_DATA_INPAGE_ERROR'
    0x7E = 'SYSTEM_THREAD_EXCEPTION_NOT_HANDLED'; 0x7F = 'UNEXPECTED_KERNEL_MODE_TRAP'; 0x9F = 'DRIVER_POWER_STATE_FAILURE'
    0xA0 = 'INTERNAL_POWER_ERROR'; 0xC2 = 'BAD_POOL_CALLER'; 0xC5 = 'DRIVER_CORRUPTED_EXPOOL'; 0xD1 = 'DRIVER_IRQL_NOT_LESS_OR_EQUAL'
    0xEF = 'CRITICAL_PROCESS_DIED'; 0xF4 = 'CRITICAL_OBJECT_TERMINATION'; 0xFC = 'ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY'
    0x101 = 'CLOCK_WATCHDOG_TIMEOUT'; 0x109 = 'CRITICAL_STRUCTURE_CORRUPTION'; 0x113 = 'VIDEO_DXGKRNL_FATAL_ERROR'
    0x116 = 'VIDEO_TDR_FAILURE'; 0x117 = 'VIDEO_TDR_TIMEOUT_DETECTED'; 0x119 = 'VIDEO_SCHEDULER_INTERNAL_ERROR'
    0x124 = 'WHEA_UNCORRECTABLE_ERROR'; 0x133 = 'DPC_WATCHDOG_VIOLATION'; 0x139 = 'KERNEL_SECURITY_CHECK_FAILURE'
    0x13A = 'KERNEL_MODE_HEAP_CORRUPTION'; 0x154 = 'UNEXPECTED_STORE_EXCEPTION'; 0x18B = 'SECURE_KERNEL_ERROR'
    0x1C8 = 'MANUALLY_INITIATED_POWER_BUTTON_HOLD'; 0x7B = 'INACCESSIBLE_BOOT_DEVICE'; 0xED = 'UNMOUNTABLE_BOOT_VOLUME'
    0x3D = 'INTERRUPT_EXCEPTION_NOT_HANDLED'; 0x1D = 'TRAP_CAUSE_UNKNOWN'; 0xC4 = 'DRIVER_VERIFIER_DETECTED_VIOLATION'
    0x10E = 'VIDEO_MEMORY_MANAGEMENT_INTERNAL'
}

function Get-PevStopName([string]$code) {
    if ($code.Length -lt 3) { return $null }
    $v = ConvertTo-PevLong $code
    if ($script:PevStopCodes.ContainsKey($v)) { $script:PevStopCodes[$v] } else { $null }
}

function Get-PevExceptionName([string]$hex) {
    switch (($hex.ToLowerInvariant() -replace '^0x', '').TrimStart('0')) {
        'c0000005' { 'access violation' } 'c0000409' { 'stack buffer overrun / fail-fast' } 'c0000374' { 'heap corruption' }
        'c00000fd' { 'stack overflow' } 'c000001d' { 'illegal instruction' } 'e0434352' { '.NET exception' }
        'e06d7363' { 'C++ exception' } '80000003' { 'breakpoint' } '40000015' { 'abort' } 'c0000602' { 'fail-fast' }
        'c0000142' { 'DLL failed to load' } 'c0000135' { 'DLL not found' } 'c000041d' { 'exception in a callback' }
        default { $null }
    }
}

# ======================================================================================
#  Rules: which events mean which problem, in plain English (same as the pxd Event Viewer app)
# ======================================================================================

$script:PevRules = @(
    @{ Id = 'bsod'; Title = 'Blue screens'; Sev = 'Critical'; Log = 'System'
        Providers = @('Microsoft-Windows-WER-SystemErrorReporting', 'BugCheck'); Ids = @(1001)
        Explain = 'Windows crashed and restarted (a stop error). The code says roughly what failed; the same code again and again points at one driver or bad hardware.'
        Fix = 'Update or roll back the driver named in the code (graphics, network, storage are the usual ones), update the BIOS, run Windows Memory Diagnostic, and check the Hardware errors and Disk errors sections. Dumps are in C:\Windows\Minidump.'
        Describe = { param($e)
            $code = ''; if (($e.Message + ' ' + ($e.Props -join ' ')) -match '0x[0-9a-fA-F]{8}') { $code = $Matches[0] }
            $name = Get-PevStopName $code
            $e.Key = if ($code -eq '') { 'unknown code' } elseif ($name) { "$($code.ToLowerInvariant()) $name" } else { $code.ToLowerInvariant() }
            $e.Summary = "Blue screen $($e.Key)" } }
    @{ Id = 'unexpected-shutdown'; Title = 'Unexpected shutdowns'; Sev = 'Critical'; Log = 'System'
        Providers = @('Microsoft-Windows-Kernel-Power'); Ids = @(41)
        Explain = 'Windows started again without having shut down properly: the power went off, the power button was held, the PC froze and was reset, or it blue-screened.'
        Fix = 'If the power button was held, ask what the user saw before (a freeze?). Otherwise check the power supply and cables, a UPS, overheating (dust, fans), and the Blue screens section.'
        Describe = { param($e)
            $bug = ConvertTo-PevLong (Get-PevProp $e 0); $button = ConvertTo-PevLong (Get-PevProp $e 6)
            if ($bug -ne 0) { $hex = '0x{0:x8}' -f $bug; $n = Get-PevStopName $hex; $e.Key = 'after a blue screen'; $e.Summary = "Restarted after a blue screen $hex$(if ($n) { " $n" })" }
            elseif ($button -ne 0) { $e.Key = 'power button held'; $e.Summary = 'Turned off by holding the power button' }
            else { $e.Key = 'power lost or reset'; $e.Summary = 'Power was cut or the PC was reset (no blue screen recorded)' } } }
    @{ Id = 'hardware'; Title = 'Hardware errors'; Sev = 'Critical'; Log = 'System'
        Providers = @('Microsoft-Windows-WHEA-Logger'); Ids = @(1, 17, 18, 19, 20, 46, 47)
        Explain = 'The processor, memory or a PCIe device reported a hardware error. Corrected ones are warnings; fatal ones cause blue screens (0x124).'
        Fix = 'Undo any overclock or XMP/EXPO memory profile, update the BIOS and chipset drivers, reseat the graphics card and memory, check temperatures. Repeated errors on one device point at that part.'
        Describe = { param($e)
            $comp = ''; if ($e.Message -match 'Component:\s*(.+)') { $comp = $Matches[1].Trim() }
            $src = ''; if ($e.Message -match 'Error Source:\s*(.+)') { $src = $Matches[1].Trim() }
            $e.Key = if ($comp) { $comp } else { Get-PevFirstLine $e.Message }
            $kind = if ($e.Id -eq 18) { 'Fatal ' } elseif ($e.Id -eq 19) { 'Corrected ' } else { '' }
            $e.Summary = ("$($kind)hardware error: $($e.Key)$(if ($src) { " ($src)" })").Trim() } }
    @{ Id = 'disk-failing'; Title = 'Drive predicted to fail'; Sev = 'Critical'; Log = 'System'
        Providers = @('disk'); Ids = @(52)
        Explain = "A drive's own health monitoring (SMART) says it is about to fail."
        Fix = "Back up that drive now, then replace it. Don't run long scans on it first."
        Describe = { param($e) $e.Key = Get-PevDevice $e.Message; $e.Summary = "SMART failure warning on $($e.Key)" } }
    @{ Id = 'memory-test'; Title = 'Memory test failures'; Sev = 'Critical'; Log = 'System'
        Providers = @('Microsoft-Windows-MemoryDiagnostics-Results'); Ids = @(1102)
        Explain = 'Windows Memory Diagnostic found errors in the RAM.'
        Fix = 'Turn off XMP/EXPO, test one memory stick at a time, and replace the faulty one.'
        Describe = { param($e) $e.Key = 'RAM'; $e.Summary = 'Windows Memory Diagnostic found memory errors' } }
    @{ Id = 'disk'; Title = 'Disk and file system errors'; Sev = 'Error'; Log = 'System'
        Providers = @('disk', 'Ntfs', 'Microsoft-Windows-Ntfs', 'stornvme', 'storahci', 'volmgr'); Ids = @(7, 11, 51, 55, 129, 137, 153, 154, 161)
        Explain = 'Reads or writes failed, a drive stopped answering and was reset, or the file system found damage. A few after a power cut can be harmless; a steady stream means a failing drive or cable.'
        Fix = "Check the drive's health (CrystalDiskInfo or 'Get-PhysicalDisk'), reseat or replace the SATA cable, update storage drivers and firmware, then run 'chkdsk C: /scan'. Back up first if errors keep coming."
        Describe = { param($e)
            $e.Key = Get-PevDevice $e.Message
            $e.Summary = switch ($e.Id) {
                7 { "Bad block on $($e.Key)" } { $_ -in 11, 51, 153, 154 } { "I/O error on $($e.Key)" } 129 { "Drive reset (stopped responding) on $($e.Key)" }
                { $_ -in 55, 137 } { "File system damage on $($e.Key)" } 161 { "Crash dump couldn't be written" } default { Get-PevFirstLine $e.Message } } } }
    @{ Id = 'app-crash'; Title = 'App crashes'; Sev = 'Error'; Log = 'Application'
        Providers = @('Application Error', '.NET Runtime'); Ids = @(1000, 1026)
        Pairs = @(@{ P = 'Application Error'; Ids = @(1000) }, @{ P = '.NET Runtime'; Ids = @(1026) })
        Explain = 'Programs that closed because of an error. The faulting module says where: the app itself, a plug-in, a graphics driver (nvwgf2umx.dll, atidxx64.dll, igd*.dll) or Windows (ntdll.dll, KERNELBASE.dll).'
        Fix = 'Update or repair the app. If the module is a graphics driver, update that driver. Many apps crashing in ntdll.dll suggests system file damage (run SFC and DISM) or bad memory.'
        Describe = { param($e)
            if ($e.Provider -eq '.NET Runtime') {
                $app = ''; if ($e.Message -match 'Application:\s*(\S+)') { $app = $Matches[1] }
                $ex = ''; if ($e.Message -match 'Exception Info:\s*([\w\.]+)') { $ex = $Matches[1] }
                $e.Key = if ($app) { $app } else { 'a .NET app' }; $e.Summary = "$($e.Key) crashed$(if ($ex) { " with $ex" })"; return }
            $e.Key = if (Get-PevProp $e 0) { Get-PevProp $e 0 } else { 'unknown app' }
            $module = Get-PevProp $e 3; $code = (Get-PevProp $e 6) -replace '0x', ''; $cn = Get-PevExceptionName $code
            $e.Summary = "$($e.Key) crashed$(if ($module) { " in $module" })$(if ($code) { " (0x$code$(if ($cn) { ", $cn" }))" })" } }
    @{ Id = 'app-hang'; Title = 'Apps that stopped responding'; Sev = 'Warning'; Log = 'Application'
        Providers = @('Application Hang'); Ids = @(1002)
        Explain = 'Programs that froze and were closed by the user or by Windows.'
        Fix = 'Look for a pattern: the same app (update it, clear its cache), or every app at once (low memory, a slow or failing disk, antivirus scanning).'
        Describe = { param($e) $e.Key = if (Get-PevProp $e 0) { Get-PevProp $e 0 } else { 'unknown app' }; $e.Summary = "$($e.Key) stopped responding" } }
    @{ Id = 'gpu-reset'; Title = 'Graphics driver resets'; Sev = 'Warning'; Log = 'System'
        Providers = @('Display'); Ids = @(4101)
        Explain = 'The graphics driver stopped responding and Windows restarted it (the screen goes black for a moment).'
        Fix = 'Clean-install the latest graphics driver (or roll back if it started after an update), check GPU temperatures, and undo any GPU overclock.'
        Describe = { param($e) $d = ''; if ($e.Message -match 'Display driver (\S+)') { $d = $Matches[1] }; $e.Key = if ($d) { $d } else { 'graphics driver' }; $e.Summary = "$($e.Key) stopped responding and was recovered" } }
    @{ Id = 'service'; Title = 'Service failures'; Sev = 'Error'; Log = 'System'
        Providers = @('Service Control Manager'); Ids = @(7000, 7001, 7009, 7011, 7022, 7023, 7024, 7031, 7034)
        Explain = 'Windows services that failed to start, timed out, or stopped unexpectedly.'
        Fix = 'Check the service in services.msc: its startup type, the account it runs as, and whether its program still exists. A service that keeps crashing usually needs its program reinstalled or updated.'
        Describe = { param($e)
            $svc = ''; if ($e.Message -match '(?m)^The (.+?) service') { $svc = $Matches[1] } elseif ($e.Message -match 'for the (.+?) service') { $svc = $Matches[1] }
            $e.Key = if ($svc) { $svc } else { Get-PevFirstLine $e.Message }
            $e.Summary = switch ($e.Id) { { $_ -in 7031, 7034 } { "$($e.Key) stopped unexpectedly" } { $_ -in 7009, 7011 } { "$($e.Key) timed out" }
                7001 { "$($e.Key) couldn't start because a service it needs failed" } default { "$($e.Key) failed to start" } } } }
    @{ Id = 'update-failed'; Title = 'Failed Windows updates'; Sev = 'Error'; Log = 'System'
        Providers = @('Microsoft-Windows-WindowsUpdateClient'); Ids = @(20, 25, 31)
        Explain = "Updates that Windows Update couldn't download or install. One failure that later succeeds is normal; the same update failing again and again is not."
        Fix = 'Restart and try again, free up disk space, then reset Windows Update (pxd Windows Tweaks, Features & fixes) and run SFC and DISM. Look up the 0x8... error code for specifics.'
        Describe = { param($e)
            $title = Get-PevProp $e 1; $err = ''
            if ($e.Message -match '(?s)error (0x[0-9A-Fa-f]{8}):\s*(.+?)\.?\s*$') { $err = $Matches[1]; $title = $Matches[2].Trim() }
            $e.Key = if ($title) { $title } else { 'an update' }; $e.Summary = "$($e.Key) failed$(if ($err) { " with $err" })" } }
    @{ Id = 'install-failed'; Title = 'Failed program installs'; Sev = 'Warning'; Log = 'Application'
        Providers = @('MsiInstaller'); Ids = @(11708, 11725)
        Explain = 'Windows Installer (.msi) setups that failed or were rolled back.'
        Fix = 'Run the installer again as administrator, check free space, and remove any older version first. Its log (msiexec /l*v) shows the exact step.'
        Describe = { param($e) $p = ''; if ($e.Message -match 'Product:\s*(.+?)\s*--') { $p = $Matches[1] }; $e.Key = if ($p) { $p } else { Get-PevFirstLine $e.Message }
            $e.Summary = "$($e.Key): $(if ($e.Id -eq 11725) { 'removal failed' } else { 'installation failed' })" } }
    @{ Id = 'low-space'; Title = 'Low disk space'; Sev = 'Warning'; Log = 'System'
        Providers = @('srv'); Ids = @(2013)
        Explain = 'A drive was at or near full. Full drives cause failed updates, crashes and slow logins.'
        Fix = 'Free space: Disk Cleanup (including system files), empty Downloads and the Recycle Bin, move large files, or remove old user profiles.'
        Describe = { param($e) $d = ''; if ($e.Message -match 'The ([A-Z]:) disk') { $d = $Matches[1] }; $e.Key = if ($d) { $d } else { 'a drive' }; $e.Summary = "$($e.Key) was nearly full" } }
    @{ Id = 'network'; Title = 'Network problems'; Sev = 'Warning'; Log = 'System'
        Providers = @('Microsoft-Windows-DNS-Client', 'Tcpip'); Ids = @(1014, 4199)
        Explain = "DNS lookups that timed out, or another device on the network using this PC's IP address."
        Fix = 'For DNS timeouts: check the DNS servers (router, domain controller) and the connection. For an IP conflict: find the other device by its MAC address, or give one of them a reserved address.'
        Describe = { param($e)
            if ($e.Id -eq 4199) { $ip = ''; if ($e.Message -match '\d+\.\d+\.\d+\.\d+') { $ip = $Matches[0] }; $e.Key = "IP conflict $ip"; $e.Summary = "Another device is using $ip"; return }
            $n = ''; if ($e.Message -match 'name (\S+) timed out') { $n = $Matches[1] }; $e.Key = if ($n) { $n } else { 'DNS' }; $e.Summary = "DNS lookup for $($e.Key) timed out" } }
    @{ Id = 'group-policy'; Title = 'Group Policy errors'; Sev = 'Warning'; Log = 'System'
        Providers = @('Microsoft-Windows-GroupPolicy'); Ids = @(1085, 1096, 1125, 1129, 7016, 7320)
        Explain = "Domain policies that couldn't be read or applied, usually because the domain controller couldn't be reached at the time."
        Fix = "Check the network and DNS (the PC must use the domain's DNS servers), then run 'gpupdate /force' and 'gpresult /h report.html'."
        Describe = { param($e) $e.Key = Get-PevTrim (Get-PevFirstLine $e.Message) 90; $e.Summary = $e.Key } }
    @{ Id = 'time'; Title = 'Clock sync problems'; Sev = 'Warning'; Log = 'System'
        Providers = @('Microsoft-Windows-Time-Service'); Ids = @(36, 129, 134)
        Explain = "Windows couldn't sync its clock. A wrong clock breaks domain logins, websites (certificate errors) and Kerberos."
        Fix = "Run 'w32tm /resync'. On a domain, check the PC can reach a domain controller; at home, pxd Windows Tweaks can switch it to pool.ntp.org."
        Describe = { param($e) $e.Key = Get-PevTrim (Get-PevFirstLine $e.Message) 90; $e.Summary = $e.Key } }
    @{ Id = 'failed-logon'; Title = 'Failed sign-ins'; Sev = 'Warning'; Log = 'Security'; NeedsAdmin = $true
        Providers = @('Microsoft-Windows-Security-Auditing'); Ids = @(4625)
        Explain = 'Sign-ins that failed: wrong passwords, unknown accounts, locked or disabled accounts. Many in a row from one address can be an attack or a saved old password.'
        Fix = 'Wrong password: check saved credentials (Credential Manager, mapped drives, phones with mail). Many from a remote address: block it and turn off exposed RDP.'
        Describe = { param($e)
            $user = Get-PevProp $e 5; $ip = Get-PevProp $e 19
            $why = switch ((Get-PevProp $e 9).ToLowerInvariant()) { '0xc000006a' { 'wrong password' } '0xc0000064' { 'no such account' } '0xc0000234' { 'account locked out' }
                '0xc0000072' { 'account disabled' } '0xc0000071' { 'password expired' } '0xc0000193' { 'account expired' } '0xc000006f' { 'outside allowed hours' }
                '0xc0000224' { 'must change password' } default { 'failed' } }
            $e.Key = "$(if ($user) { $user } else { '?' }): $why"; $e.Summary = "$user $why$(if ($ip.Length -gt 1) { " from $ip" })" } }
    @{ Id = 'restarts'; Title = 'Restarts and shutdowns'; Sev = 'Info'; Log = 'System'
        Providers = @('User32'); Ids = @(1074)
        Explain = 'Who or what restarted or shut down the PC, and why. Answers "it restarted on its own".'
        Fix = 'Restarts by TrustedInstaller or MoUsoCoreWorker are Windows Update; set active hours to control when they happen.'
        Describe = { param($e)
            $proc = ''; try { $proc = [IO.Path]::GetFileName(((Get-PevProp $e 0) -split ' ')[0]) } catch { }
            $user = Get-PevProp $e 6; $kind = Get-PevProp $e 4; $reason = Get-PevProp $e 2
            $e.Key = if ($proc) { $proc } else { 'unknown' }
            $e.Summary = "$(if ($kind) { $kind } else { 'restart' }) by $(if ($user) { $user } else { '?' }) via $($e.Key)$(if ($reason) { ': ' + (Get-PevTrim $reason 70) })" } }
)

# ======================================================================================
#  Reading the logs
# ======================================================================================

function Get-PevXPath($rule, [long]$ms) {
    $time = "TimeCreated[timediff(@SystemTime) <= $ms]"
    if ($rule.Pairs) {
        $parts = foreach ($p in $rule.Pairs) { "(Provider[@Name='$($p.P)'] and ($(($p.Ids | ForEach-Object { "EventID=$_" }) -join ' or ')))" }
        return "*[System[($($parts -join ' or ')) and $time]]"
    }
    $prov = ($rule.Providers | ForEach-Object { "Provider[@Name='$_']" }) -join ' or '
    $ids = ($rule.Ids | ForEach-Object { "EventID=$_" }) -join ' or '
    "*[System[($prov) and ($ids) and $time]]"
}

# Newest first. Throws UnauthorizedAccessException when the log needs admin rights.
function Read-PevEvents([string]$Log, [string]$XPath, [int]$Max) {
    $list = New-Object System.Collections.Generic.List[object]
    try {
        $records = Get-WinEvent -LogName $Log -FilterXPath $XPath -MaxEvents $Max -ErrorAction Stop
    } catch {
        $ex = $_.Exception
        if ($ex -is [UnauthorizedAccessException] -or $_.FullyQualifiedErrorId -match 'UnauthorizedAccess') { throw [UnauthorizedAccessException]::new('Needs administrator rights') }
        if ($_.FullyQualifiedErrorId -match 'NoMatchingEventsFound' -or $ex.Message -match 'No events were found|There is not an event log') { return , $list }
        throw
    }
    foreach ($r in $records) {
        $props = @(); try { $props = @($r.Properties | ForEach-Object { if ($null -eq $_.Value) { '' } else { [string]$_.Value } }) } catch { }
        $msg = ''; try { $msg = $r.Message } catch { }
        if (-not $msg) { $msg = if ($props.Count) { $props -join ' | ' } else { "(No description for event $($r.Id) from $($r.ProviderName).)" } }
        $lvl = 4; if ($null -ne $r.Level) { $lvl = [int]$r.Level }
        $list.Add([pscustomobject]@{
                Time = $r.TimeCreated; Log = $Log; Provider = [string]$r.ProviderName; Id = [int]$r.Id; Level = $lvl
                Message = ($msg -replace "`r`n", "`n").Trim(); Props = $props; Key = ''; Summary = ''; RuleId = $null })
    }
    , $list
}

function Set-PevDescription($rule, $e) {
    $e.RuleId = $rule.Id
    try { & $rule.Describe $e } catch { }
    if (-not $e.Key) { $e.Key = Get-PevTrim (Get-PevFirstLine $e.Message) 90 }
    if (-not $e.Summary) { $e.Summary = Get-PevTrim (Get-PevFirstLine $e.Message) 140 }
}

function Get-PevRuleFor($e) {
    foreach ($r in $script:PevRules) {
        if ($r.Log -ne $e.Log) { continue }
        if ($r.Pairs) { foreach ($p in $r.Pairs) { if ($p.P -eq $e.Provider -and $p.Ids -contains $e.Id) { return $r } } }
        elseif ($r.Ids -contains $e.Id -and $r.Providers -contains $e.Provider) { return $r }
    }
    $null
}

function Get-PevDigest([int]$Days, [switch]$Quiet) {
    $ms = [long]([TimeSpan]::FromDays($Days).TotalMilliseconds)
    $admin = Test-PevAdmin
    $results = New-Object System.Collections.Generic.List[object]
    $i = 0
    foreach ($r in $script:PevRules) {
        $i++
        if (-not $Quiet) { Write-Host ("`r  Reading the event logs... {0}/{1}  {2}" -f $i, $script:PevRules.Count, $r.Title).PadRight(78) -NoNewline -ForegroundColor DarkCyan }
        $res = [pscustomobject]@{ Rule = $r; Items = (New-Object System.Collections.Generic.List[object]); Error = $null; Capped = $false }
        if ($r.NeedsAdmin -and -not $admin) { $res.Error = 'Needs administrator rights' }
        else {
            try {
                $events = Read-PevEvents $r.Log (Get-PevXPath $r $ms) ($script:PevMax + 1)
                foreach ($e in $events) { Set-PevDescription $r $e; $res.Items.Add($e) }
                if ($res.Items.Count -gt $script:PevMax) { $res.Capped = $true; $res.Items.RemoveAt($res.Items.Count - 1) }
            } catch [UnauthorizedAccessException] { $res.Error = 'Needs administrator rights' }
            catch { $res.Error = $_.Exception.Message }
        }
        $results.Add($res)
    }
    if (-not $Quiet) { Write-Host ("`r" + (' ' * 78) + "`r") -NoNewline }
    [pscustomobject]@{ Days = $Days; TakenAt = Get-Date; Results = $results.ToArray(); Admin = $admin }
}

function Get-PevTop($res, [int]$n = 5) {
    $res.Items | Group-Object Key | ForEach-Object {
        [pscustomobject]@{ Key = $_.Group[0].Key; Count = $_.Count; Last = ($_.Group | Measure-Object Time -Maximum).Maximum }
    } | Sort-Object @{ e = 'Count'; Descending = $true }, @{ e = 'Last'; Descending = $true } | Select-Object -First $n
}

function Get-PevCount($digest, [string]$id) { $r = $digest.Results | Where-Object { $_.Rule.Id -eq $id }; if ($r) { $r.Items.Count } else { 0 } }

# ======================================================================================
#  PC snapshot
# ======================================================================================

function Get-PevPendingReboot {
    $reasons = New-Object System.Collections.Generic.List[string]
    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') { $reasons.Add('Windows component servicing') }
    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') { $reasons.Add('Windows Update') }
    try { $v = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction Stop).PendingFileRenameOperations; if ($v) { $reasons.Add('Files waiting to be replaced') } } catch { }
    try {
        $a = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ActiveComputerName' -ErrorAction Stop).ComputerName
        $p = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\ComputerName\ComputerName' -ErrorAction Stop).ComputerName
        if ($a -and $p -and $a -ne $p) { $reasons.Add('Computer rename') }
    } catch { }
    if (Test-Path 'HKLM:\SOFTWARE\Microsoft\Updates\UpdateExeVolatile') { $reasons.Add('Software update') }
    , $reasons.ToArray()
}

# The people signed in at the PC (console and Remote Desktop): owners of each explorer.exe, else the console user.
# Run from a remote PowerShell, this is the person at the PC, not the admin running the report.
function Get-PevSignedIn {
    $users = New-Object System.Collections.Generic.List[string]
    foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name = 'explorer.exe'" -OperationTimeoutSec 15 -ErrorAction SilentlyContinue)) {
        try {
            $o = Invoke-CimMethod -InputObject $p -MethodName GetOwner -ErrorAction Stop
            if ($o.ReturnValue -eq 0 -and $o.User) { $users.Add($(if ($o.Domain) { "$($o.Domain)\$($o.User)" } else { $o.User })) }
        } catch { }
    }
    if ($users.Count -eq 0) {
        $cu = (Get-CimInstance Win32_ComputerSystem -OperationTimeoutSec 15 -ErrorAction SilentlyContinue).UserName
        if ($cu) { $users.Add($cu) }
    }
    , @($users | Sort-Object -Unique)
}

function Get-PevUserLine($signedIn, [string]$runBy) {
    $signedIn = @($signedIn)
    if ($signedIn.Count -eq 0) { return "Nobody signed in (report run by $runBy)" }
    $list = $signedIn -join ', '
    if ($signedIn.Count -eq 1 -and $signedIn[0] -eq $runBy) { return $list }
    "$list (report run by $runBy)"
}

function Get-PevSnapshot {
    $s = [ordered]@{ TakenAt = Get-Date; ComputerName = $env:COMPUTERNAME; User = "$env:USERDOMAIN\$env:USERNAME"; Problems = (New-Object System.Collections.Generic.List[string]) }
    function Try-Part([string]$what, [scriptblock]$sb) { try { & $sb } catch { $s.Problems.Add("${what}: $($_.Exception.Message)") } }
    Try-Part 'Signed-in users' { $s.User = Get-PevUserLine (Get-PevSignedIn) $s.User }

    Try-Part 'Windows' {
        $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        $name = $cv.ProductName; $build = [string]$cv.CurrentBuildNumber
        if ([int]$build -ge 22000) { $name = $name -replace 'Windows 10', 'Windows 11' }
        $s.OsName = $name
        $s.OsVersion = if ($cv.DisplayVersion) { $cv.DisplayVersion } else { $cv.ReleaseId }
        $s.OsBuild = if ($null -ne $cv.UBR) { "$build.$($cv.UBR)" } else { $build }
        if ($cv.InstallDate) { $s.InstallDate = ([DateTimeOffset]::FromUnixTimeSeconds([uint32]$cv.InstallDate)).LocalDateTime }
        $dom = [Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().DomainName
        $s.DomainJoined = [bool]$dom
        $s.Domain = if ($dom) { $dom } elseif ($env:USERDOMAIN -eq $env:COMPUTERNAME) { 'WORKGROUP' } else { $env:USERDOMAIN }
    }
    Try-Part 'Uptime' {
        # TickCount64 (PowerShell 7) counts from the real boot even with Fast Startup; Windows PowerShell 5.1 lacks it.
        $tc = [Environment]::TickCount64
        if ($null -ne $tc) { $s.Uptime = [TimeSpan]::FromMilliseconds($tc); $s.LastBoot = (Get-Date) - $s.Uptime }
        else { $os = Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 15; $s.LastBoot = $os.LastBootUpTime; $s.Uptime = (Get-Date) - $os.LastBootUpTime }
    }
    Try-Part 'Pending reboot' { $s.PendingReboot = Get-PevPendingReboot }
    Try-Part 'Computer' {
        $cs = Get-CimInstance Win32_ComputerSystem -OperationTimeoutSec 15
        $s.Manufacturer = "$($cs.Manufacturer)".Trim(); $s.Model = "$($cs.Model)".Trim()
        $bios = Get-CimInstance Win32_BIOS -OperationTimeoutSec 15
        $s.Serial = "$($bios.SerialNumber)".Trim()
        $s.Bios = "$($bios.SMBIOSBIOSVersion)".Trim() + $(if ($bios.ReleaseDate) { ' (' + ([datetime]$bios.ReleaseDate).ToString('yyyy-MM-dd') + ')' })
        if ($s.Serial -in 'To be filled by O.E.M.', 'Default string', 'System Serial Number', '0') { $s.Serial = '' }
        if ($s.Model -in '', 'System Product Name', 'To be filled by O.E.M.', 'Default string') {
            $bb = Get-CimInstance Win32_BaseBoard -OperationTimeoutSec 15 | Select-Object -First 1
            $s.Manufacturer = "$($bb.Manufacturer)".Trim(); $s.Model = "$($bb.Product)".Trim() + ' (motherboard)'
        }
    }
    Try-Part 'Processor' {
        $s.Cpu = "$([Microsoft.Win32.Registry]::GetValue('HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0', 'ProcessorNameString', ''))".Trim()
        $s.Threads = [Environment]::ProcessorCount
        $s.Cores = [int](Get-CimInstance Win32_Processor -OperationTimeoutSec 15 | Measure-Object NumberOfCores -Sum).Sum
    }
    Try-Part 'Memory' {
        $os = Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 15
        $s.RamTotal = [double]$os.TotalVisibleMemorySize * 1KB; $s.RamFree = [double]$os.FreePhysicalMemory * 1KB
    }
    Try-Part 'Disks' {
        $s.Disks = @([IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady } | ForEach-Object {
                [pscustomobject]@{ Name = $_.Name.TrimEnd('\'); Label = $_.VolumeLabel; Total = [double]$_.TotalSize; Free = [double]$_.AvailableFreeSpace; Format = $_.DriveFormat } })
    }
    Try-Part 'Network' {
        $ads = foreach ($n in [Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
            if ($n.OperationalStatus -ne 'Up' -or $n.NetworkInterfaceType -in 'Loopback', 'Tunnel') { continue }
            $p = $n.GetIPProperties()
            $v4 = @($p.UnicastAddresses | Where-Object { $_.Address.AddressFamily -eq 'InterNetwork' })
            if ($v4.Count -eq 0) { continue }
            $dhcp = $false; try { $dhcp = $p.GetIPv4Properties().IsDhcpEnabled } catch { }
            $kind = switch ([string]$n.NetworkInterfaceType) { 'Wireless80211' { 'Wi-Fi' } { $_ -in 'Ethernet', 'GigabitEthernet', 'FastEthernetT' } { 'Ethernet' } 'Ppp' { 'VPN' } default { [string]$n.NetworkInterfaceType } }
            if ($n.Description -match 'Virtual|Hyper-V') { $kind = 'Virtual' }
            $speed = if ($n.Speed -ge 1e9) { '{0:0.#} Gbps' -f ($n.Speed / 1e9) } elseif ($n.Speed -gt 0) { '{0} Mbps' -f [int]($n.Speed / 1e6) } else { '' }
            [pscustomobject]@{
                Name = $n.Name; Kind = $kind; Speed = $speed; Dhcp = $dhcp
                Ipv4 = ($v4 | ForEach-Object { "$($_.Address)/$($_.PrefixLength)" }) -join ', '
                Gateway = (@($p.GatewayAddresses | Where-Object { $_.Address.AddressFamily -eq 'InterNetwork' }) | ForEach-Object { "$($_.Address)" }) -join ', '
                Dns = (@($p.DnsAddresses | Where-Object { $_.AddressFamily -eq 'InterNetwork' }) | ForEach-Object { "$_" }) -join ', '
                Mac = ($n.GetPhysicalAddress().GetAddressBytes() | ForEach-Object { $_.ToString('X2') }) -join '-'
            }
        }
        $s.Adapters = @($ads | Sort-Object @{ e = { $_.Kind -eq 'Virtual' } }, @{ e = { $_.Gateway.Length -eq 0 } })
    }
    Try-Part 'Updates' {
        $s.Hotfixes = @(Get-CimInstance Win32_QuickFixEngineering -OperationTimeoutSec 20 | ForEach-Object {
                $when = $null; try { if ($_.InstalledOn) { $when = [datetime]$_.InstalledOn } } catch { }
                [pscustomobject]@{ Id = $_.HotFixID; Description = $_.Description; InstalledOn = $when } } | Sort-Object @{ e = { if ($_.InstalledOn) { $_.InstalledOn } else { [datetime]::MinValue } }; Descending = $true })
    }
    Try-Part 'Antivirus' {
        $names = @(); try { $names = @(Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct -OperationTimeoutSec 10 -ErrorAction Stop | ForEach-Object { $_.displayName }) } catch { }
        $s.Antivirus = if ($names.Count) { ($names | Select-Object -Unique) -join ', ' } else { 'None reported' }
        try {
            $mp = Get-CimInstance -Namespace root\Microsoft\Windows\Defender -ClassName MSFT_MpComputerStatus -OperationTimeoutSec 10 -ErrorAction Stop
            if ($mp.AntivirusSignatureLastUpdated) {
                $s.Defender = "Defender $(if ($mp.RealTimeProtectionEnabled) { 'real-time on' } else { 'real-time OFF' }), signatures $(Get-PevAgo ((Get-Date) - $mp.AntivirusSignatureLastUpdated)) old"
            }
        } catch { }
    }
    [pscustomobject]$s
}

# ======================================================================================
#  Text (console, ticket, report)
# ======================================================================================

function Get-PevSevColor([string]$sev) { switch ($sev) { 'Critical' { 'Red' } 'Error' { 'Magenta' } 'Warning' { 'Yellow' } default { 'Cyan' } } }

function Get-PevBar([double]$used, [int]$width = 20) {
    $f = [int][Math]::Round($width * [Math]::Min(1, [Math]::Max(0, $used)))
    ([string][char]0x2588) * $f + ([string][char]0x2591) * ($width - $f)
}

# Snapshot as label/value lines (shared by the console, the ticket text and the report).
function Get-PevSnapshotLines($s) {
    $l = New-Object System.Collections.Generic.List[object]
    function Add([string]$k, [string]$v, [string]$warn = '') { $l.Add([pscustomobject]@{ K = $k; V = $v; Warn = $warn }) }
    Add 'Computer' "$($s.ComputerName) ($(if ($s.DomainJoined) { "domain $($s.Domain)" } else { $s.Domain }))"
    Add 'Signed in' $s.User
    Add 'Windows' "$($s.OsName) $($s.OsVersion) (build $($s.OsBuild))$(if ($s.InstallDate) { ', installed ' + $s.InstallDate.ToString('yyyy-MM-dd') })"
    if ($s.Uptime) { Add 'Uptime' "$(Get-PevAgo $s.Uptime) (last boot $($s.LastBoot.ToString('yyyy-MM-dd HH:mm')))" $(if ($s.Uptime.TotalDays -ge 14) { 'long uptime' }) }
    $pr = @($s.PendingReboot)
    Add 'Pending reboot' $(if ($pr.Count) { 'Yes: ' + ($pr -join ', ') } else { 'No' }) $(if ($pr.Count) { 'restart needed' })
    Add 'Hardware' (("$($s.Manufacturer) $($s.Model)".Trim()) + $(if ($s.Serial) { ", serial $($s.Serial)" }) + $(if ($s.Bios) { ", BIOS $($s.Bios)" }))
    if ($s.Cpu) { Add 'Processor' "$($s.Cpu) ($($s.Cores) cores, $($s.Threads) threads)" }
    if ($s.RamTotal) {
        $used = ($s.RamTotal - $s.RamFree) / $s.RamTotal
        Add 'Memory' ('{0} of {1} in use ({2:0}%)' -f (Get-PevSize ($s.RamTotal - $s.RamFree)), (Get-PevSize $s.RamTotal), ($used * 100)) $(if ($used -ge 0.9) { 'memory nearly full' })
    }
    foreach ($d in @($s.Disks)) {
        $pct = if ($d.Total) { 100 * $d.Free / $d.Total } else { 0 }
        Add "Disk $($d.Name)" ('{0} free of {1} ({2:0}%){3}' -f (Get-PevSize $d.Free), (Get-PevSize $d.Total), $pct, $(if ($d.Label) { ", $($d.Label)" })) $(if ($pct -lt 10) { 'low space' })
    }
    foreach ($a in @($s.Adapters)) {
        Add "$($a.Kind)" ("$($a.Name): $($a.Ipv4)" + $(if ($a.Gateway) { ", gateway $($a.Gateway)" }) + $(if ($a.Dns) { ", DNS $($a.Dns)" }) + ", $(if ($a.Dhcp) { 'DHCP' } else { 'static' }), MAC $($a.Mac)$(if ($a.Speed) { ", $($a.Speed)" })")
    }
    Add 'Antivirus' ("$($s.Antivirus)" + $(if ($s.Defender) { "; $($s.Defender)" })) $(if ($s.Defender -match 'OFF') { 'real-time protection off' })
    $hf = @($s.Hotfixes)
    if ($hf.Count) {
        $last = $hf[0]
        $age = if ($last.InstalledOn) { ((Get-Date) - $last.InstalledOn).TotalDays } else { 0 }
        Add 'Updates' (($hf | Select-Object -First 3 | ForEach-Object { "$($_.Id)$(if ($_.InstalledOn) { ' ' + $_.InstalledOn.ToString('yyyy-MM-dd') })" }) -join ', ') $(if ($age -gt 45) { 'no updates in {0:0} days' -f $age })
    }
    , $l.ToArray()
}

function Get-PevDigestLines($digest) {
    $l = New-Object System.Collections.Generic.List[string]
    $clear = New-Object System.Collections.Generic.List[string]
    foreach ($r in $digest.Results) {
        if ($r.Error) { $l.Add("  $($r.Rule.Title): not read ($($r.Error))"); continue }
        if ($r.Items.Count -eq 0) { $clear.Add($r.Rule.Title); continue }
        $last = ($r.Items | Measure-Object Time -Maximum).Maximum
        $top = (Get-PevTop $r 4 | ForEach-Object { if ($_.Count -gt 1) { "$($_.Key) x$($_.Count)" } else { $_.Key } }) -join '; '
        $l.Add(("  {0}: {1}{2} (latest {3}) - {4}" -f $r.Rule.Title, $r.Items.Count, $(if ($r.Capped) { '+' }), $last.ToString('yyyy-MM-dd HH:mm'), $top))
    }
    if ($clear.Count) { $l.Add("  All clear: $($clear -join ', ')") }
    , $l.ToArray()
}

function Get-PevTicketText($s, $digest) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("PC snapshot - $($s.ComputerName) - $($s.TakenAt.ToString('yyyy-MM-dd HH:mm'))")
    foreach ($x in (Get-PevSnapshotLines $s)) { [void]$sb.AppendLine(('{0,-15}{1}{2}' -f ($x.K + ':'), $x.V, $(if ($x.Warn) { "  [$($x.Warn)]" }))) }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("Problems in the last $($digest.Days) day(s):")
    foreach ($line in (Get-PevDigestLines $digest)) { [void]$sb.AppendLine($line) }
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("pxd Event Viewer CLI $($script:PevVersion) - $($script:PevUrl)")
    $sb.ToString()
}

function ConvertTo-PevHtml($s, $digest) {
    function Get-PevEnc([string]$t) { [Net.WebUtility]::HtmlEncode($t) }
    $rows = (Get-PevSnapshotLines $s | ForEach-Object { "<tr><th>$(Get-PevEnc $_.K)</th><td>$(Get-PevEnc $_.V)$(if ($_.Warn) { " <span class='warn'>$(Get-PevEnc $_.Warn)</span>" })</td></tr>" }) -join "`n"
    $cards = foreach ($r in $digest.Results) {
        if ($r.Error -or $r.Items.Count -eq 0) { continue }
        $sev = $r.Rule.Sev.ToLowerInvariant()
        $top = (Get-PevTop $r 8 | ForEach-Object { "<li>$(Get-PevEnc $_.Key)$(if ($_.Count -gt 1) { " <b>x$($_.Count)</b>" }) <span class='muted'>latest $($_.Last.ToString('yyyy-MM-dd HH:mm'))</span></li>" }) -join ''
        $evs = ($r.Items | Select-Object -First 25 | ForEach-Object { "<tr><td>$($_.Time.ToString('yyyy-MM-dd HH:mm'))</td><td>$(Get-PevEnc $_.Summary)</td><td class='muted'>$(Get-PevEnc $_.Provider) $($_.Id)</td></tr>" }) -join ''
        "<section class='card $sev'><h3>$(Get-PevEnc $r.Rule.Title) <span class='n'>$($r.Items.Count)$(if ($r.Capped) { '+' })</span></h3><p>$(Get-PevEnc $r.Rule.Explain)</p><p><b>Try:</b> $(Get-PevEnc $r.Rule.Fix)</p><ul>$top</ul><details><summary>Events</summary><table>$evs</table></details></section>"
    }
    $clear = ($digest.Results | Where-Object { -not $_.Error -and $_.Items.Count -eq 0 } | ForEach-Object { Get-PevEnc $_.Rule.Title }) -join ', '
    $notRead = ($digest.Results | Where-Object { $_.Error } | ForEach-Object { "$(Get-PevEnc $_.Rule.Title) ($(Get-PevEnc $_.Error))" }) -join ', '
    @"
<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>pxd Event Viewer - $(Get-PevEnc $s.ComputerName)</title>
<style>
:root{--bg:#0e1015;--card:#161922;--line:#272c39;--text:#e8eaf0;--muted:#8a92a6;--accent:#89cff0;--crit:#f2666a;--err:#e879c6;--warn:#f5b14c;--info:#00c8ff}
@media (prefers-color-scheme:light){:root{--bg:#f6f7fa;--card:#fff;--line:#dde1ea;--text:#1a1d24;--muted:#5d6577}}
body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif;padding:24px 16px}
main{max-width:980px;margin:0 auto}h1{font-size:22px;margin:0 0 4px}h2{font-size:16px;margin:28px 0 10px;color:var(--accent)}
.muted{color:var(--muted)}table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:5px 8px;border-bottom:1px solid var(--line);vertical-align:top}
th{width:150px;color:var(--muted);font-weight:600}.warn{color:var(--warn);font-weight:600}
.card{background:var(--card);border:1px solid var(--line);border-left:4px solid var(--info);border-radius:10px;padding:12px 16px;margin:10px 0}
.card.critical{border-left-color:var(--crit)}.card.error{border-left-color:var(--err)}.card.warning{border-left-color:var(--warn)}
.card h3{margin:0 0 6px;font-size:15px}.n{float:right;color:var(--muted)}.card p{margin:4px 0}summary{cursor:pointer;color:var(--accent)}
@media (max-width:600px){th{width:auto}}
</style></head><body><main>
<h1>$(Get-PevEnc $s.ComputerName)</h1><div class="muted">Report taken $($s.TakenAt.ToString('yyyy-MM-dd HH:mm')) - problems from the last $($digest.Days) day(s)$(if (-not $digest.Admin) { ' - not elevated, so failed sign-ins were not read' })</div>
<h2>PC snapshot</h2><table>$rows</table>
<h2>Problems</h2>$($cards -join "`n")
$(if ($clear) { "<p><b>All clear:</b> $clear</p>" })$(if ($notRead) { "<p class='muted'>Not read: $notRead</p>" })
<p class="muted">pxd Event Viewer CLI $($script:PevVersion) - $($script:PevUrl)</p>
</main></body></html>
"@
}

function Export-PevReport([string]$Path, $s, $digest) {
    $full = [IO.Path]::GetFullPath($Path)
    switch ([IO.Path]::GetExtension($full).ToLowerInvariant()) {
        '.html' { [IO.File]::WriteAllText($full, (ConvertTo-PevHtml $s $digest), (New-Object Text.UTF8Encoding $false)) }
        '.htm' { [IO.File]::WriteAllText($full, (ConvertTo-PevHtml $s $digest), (New-Object Text.UTF8Encoding $false)) }
        '.json' {
            $out = [ordered]@{ snapshot = $s; days = $digest.Days; problems = @($digest.Results | ForEach-Object {
                        [ordered]@{ id = $_.Rule.Id; title = $_.Rule.Title; severity = $_.Rule.Sev; count = $_.Items.Count; error = $_.Error
                            events = @($_.Items | ForEach-Object { [ordered]@{ time = $_.Time.ToString('o'); provider = $_.Provider; id = $_.Id; summary = $_.Summary; message = $_.Message } }) } }) }
            [IO.File]::WriteAllText($full, ($out | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding $false))
        }
        default { [IO.File]::WriteAllText($full, (Get-PevTicketText $s $digest), (New-Object Text.UTF8Encoding $false)) }
    }
    $full
}

# ======================================================================================
#  Console
# ======================================================================================

$script:PevEof = $false
function Read-PevKey([string]$Prompt) {
    $k = Read-Host $Prompt
    if ($null -eq $k) { $script:PevEof = $true; return 'q' }   # piped input ran out
    $k.Trim()
}

function Write-PevHeader([string]$title) {
    Write-Host ''
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host ('  ' + ('-' * [Math]::Min(76, $title.Length + 2))) -ForegroundColor DarkGray
}

function Show-PevSnapshot($s) {
    Write-PevHeader "PC snapshot - $($s.ComputerName)"
    foreach ($x in (Get-PevSnapshotLines $s)) {
        Write-Host ('  {0,-15}' -f $x.K) -NoNewline -ForegroundColor DarkGray
        Write-Host $x.V -NoNewline
        if ($x.Warn) { Write-Host "  [$($x.Warn)]" -ForegroundColor Yellow } else { Write-Host '' }
    }
    foreach ($d in @($s.Disks)) {
        if (-not $d.Total) { continue }
        $used = 1 - $d.Free / $d.Total
        Write-Host ('  {0,-15}' -f '') -NoNewline
        Write-Host (Get-PevBar $used 30) -ForegroundColor $(if ($used -ge 0.9) { 'Red' } elseif ($used -ge 0.8) { 'Yellow' } else { 'DarkCyan' }) -NoNewline
        Write-Host "  $($d.Name)" -ForegroundColor DarkGray
    }
    foreach ($p in $s.Problems) { Write-Host "  (couldn't read $p)" -ForegroundColor DarkGray }
}

function Show-PevTiles($digest, $s) {
    $tiles = @(
        @('Blue screens', 'bsod'), @('Unexpected shutdowns', 'unexpected-shutdown'), @('App crashes', 'app-crash'),
        @('Disk errors', 'disk'), @('Failed updates', 'update-failed'), @('Hardware errors', 'hardware'))
    Write-Host '  ' -NoNewline
    foreach ($t in $tiles) {
        $n = Get-PevCount $digest $t[1]
        Write-Host "$($t[0]) " -NoNewline -ForegroundColor DarkGray
        Write-Host $n -NoNewline -ForegroundColor $(if ($n -gt 0) { if ($t[1] -in 'bsod', 'unexpected-shutdown', 'hardware') { 'Red' } else { 'Yellow' } } else { 'Green' })
        Write-Host '   ' -NoNewline
    }
    $pr = @($s.PendingReboot)
    Write-Host 'Pending reboot ' -NoNewline -ForegroundColor DarkGray
    if ($pr.Count) { Write-Host 'Yes' -ForegroundColor Yellow } else { Write-Host 'No' -ForegroundColor Green }
}

function Show-PevDigest($digest) {
    Write-PevHeader "Problems in the last $($digest.Days) day(s)"
    $script:PevMenu = @()
    $n = 0
    foreach ($r in $digest.Results) {
        if ($r.Error -or $r.Items.Count -eq 0) { continue }
        $n++; $script:PevMenu += $r
        $mark = switch ($r.Rule.Sev) { 'Critical' { '!!' } 'Error' { '! ' } 'Warning' { '~ ' } default { 'i ' } }
        $last = ($r.Items | Measure-Object Time -Maximum).Maximum
        $top = (Get-PevTop $r 3 | ForEach-Object { if ($_.Count -gt 1) { "$($_.Key) x$($_.Count)" } else { $_.Key } }) -join '; '
        Write-Host ('  {0,2}  ' -f $n) -NoNewline -ForegroundColor White
        Write-Host $mark -NoNewline -ForegroundColor (Get-PevSevColor $r.Rule.Sev)
        Write-Host (' {0,-29}' -f (Get-PevTrim $r.Rule.Title 29)) -NoNewline -ForegroundColor (Get-PevSevColor $r.Rule.Sev)
        Write-Host ('{0,5}  ' -f "$($r.Items.Count)$(if ($r.Capped) { '+' })") -NoNewline
        Write-Host ('{0,-13}' -f ((Get-PevAgo ((Get-Date) - $last)) + ' ago')) -NoNewline -ForegroundColor DarkGray
        Write-Host (Get-PevTrim $top 30) -ForegroundColor Gray
    }
    if ($n -eq 0) { Write-Host '  Nothing to report.' -ForegroundColor Green }
    $clear = @($digest.Results | Where-Object { -not $_.Error -and $_.Items.Count -eq 0 } | ForEach-Object { $_.Rule.Title })
    if ($clear.Count) { Write-Host "  All clear: $($clear -join ', ')" -ForegroundColor DarkGreen }
    foreach ($r in @($digest.Results | Where-Object { $_.Error })) { Write-Host "  $($r.Rule.Title): not read ($($r.Error))" -ForegroundColor DarkGray }
}

function Write-PevWrapped([string]$text, [string]$indent = '  ', [string]$color = 'Gray') {
    $width = 96; try { $width = [Math]::Max(40, [Console]::WindowWidth - 4) } catch { }
    foreach ($para in ($text -split "`n")) {
        $line = ''
        foreach ($w in ($para -split ' ')) {
            if (($indent.Length + $line.Length + $w.Length + 1) -gt $width -and $line) { Write-Host ($indent + $line) -ForegroundColor $color; $line = $w }
            else { $line = if ($line) { "$line $w" } else { $w } }
        }
        Write-Host ($indent + $line) -ForegroundColor $color
    }
}

function Show-PevEvent($e) {
    $r = Get-PevRuleFor $e
    Write-PevHeader "$($e.Time.ToString('yyyy-MM-dd HH:mm:ss'))  $($e.Provider)  event $($e.Id)  ($($e.Log))"
    if ($e.Summary) { Write-Host "  $($e.Summary)" -ForegroundColor White }
    if ($r) { Write-PevWrapped $r.Explain '  ' 'Gray'; Write-PevWrapped ('Try: ' + $r.Fix) '  ' 'Green' }
    Write-Host ''
    Write-PevWrapped $e.Message '    ' 'DarkGray'
    [void](Read-PevKey '  Enter to go back')
}

function Show-PevList($title, $items, [string]$explain, [string]$fix) {
    $page = 0; $size = 25
    while (-not $script:PevEof) {
        Write-PevHeader $title
        if ($explain) { Write-PevWrapped $explain '  ' 'Gray' }
        if ($fix) { Write-PevWrapped ('Try: ' + $fix) '  ' 'Green' }
        if ($explain) { Write-Host '' }
        $from = $page * $size
        $slice = @($items | Select-Object -Skip $from -First $size)
        $i = $from
        foreach ($e in $slice) {
            $i++
            Write-Host ('  {0,3}  ' -f $i) -NoNewline -ForegroundColor White
            Write-Host ($e.Time.ToString('MM-dd HH:mm') + '  ') -NoNewline -ForegroundColor DarkGray
            $lvl = switch ($e.Level) { 1 { 'Critical' } 2 { 'Error' } 3 { 'Warning' } default { 'Info' } }
            $text = if ($e.Summary) { $e.Summary } else { Get-PevFirstLine $e.Message }
            Write-Host (Get-PevTrim $text 90) -ForegroundColor (Get-PevSevColor $lvl)
        }
        if (@($items).Count -eq 0) { Write-Host '  No events.' -ForegroundColor DarkGray }
        $more = ($from + $size) -lt @($items).Count
        $k = Read-PevKey ("  [number] open$(if ($more) { '  N next page' })$(if ($page -gt 0) { '  P previous' })  Enter back")
        if ($k -eq '' -or $k -eq 'q') { return }
        if ($k -eq 'n' -and $more) { $page++; continue }
        if ($k -eq 'p' -and $page -gt 0) { $page--; continue }
        $num = 0
        if ([int]::TryParse($k, [ref]$num) -and $num -ge 1 -and $num -le @($items).Count) { Show-PevEvent (@($items)[$num - 1]) }
    }
}

$script:PevLogs = @('System', 'Application', 'Security', 'Setup', 'Microsoft-Windows-PowerShell/Operational', 'Microsoft-Windows-TaskScheduler/Operational',
    'Microsoft-Windows-WindowsUpdateClient/Operational', 'Microsoft-Windows-Windows Defender/Operational',
    'Microsoft-Windows-TerminalServices-LocalSessionManager/Operational', 'Microsoft-Windows-Diagnostics-Performance/Operational')

function Get-PevBrowse([string]$Log, [int[]]$Levels, [int]$Days, [string]$Search, [int]$Max = 200) {
    $ms = [long]([TimeSpan]::FromDays($Days).TotalMilliseconds)
    $lv = ''
    if ($Levels.Count -gt 0 -and $Levels.Count -lt 4) { $lv = ' and (' + (($Levels | ForEach-Object { if ($_ -eq 4) { 'Level=0 or Level=4' } else { "Level=$_" } }) -join ' or ') + ')' }
    $xp = "*[System[TimeCreated[timediff(@SystemTime) <= $ms]$lv]]"
    $n = $Max; if ($Search) { $n = 20000 }
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($e in (Read-PevEvents $Log $xp $n)) {
        if ($Search -and -not ($e.Message -like "*$Search*" -or $e.Provider -like "*$Search*" -or "$($e.Id)" -eq $Search.Trim())) { continue }
        $r = Get-PevRuleFor $e; if ($r) { Set-PevDescription $r $e }
        $list.Add($e)
        if ($list.Count -ge $Max) { break }
    }
    , $list.ToArray()
}

function Show-PevBrowse([int]$Days) {
    Write-PevHeader 'Browse a log'
    for ($i = 0; $i -lt $script:PevLogs.Count; $i++) { Write-Host ('  {0,2}  {1}' -f ($i + 1), $script:PevLogs[$i]) }
    $k = Read-PevKey '  Log number or name (Enter = System)'
    if ($script:PevEof) { return }
    $log = 'System'; $num = 0
    if ([int]::TryParse($k, [ref]$num) -and $num -ge 1 -and $num -le $script:PevLogs.Count) { $log = $script:PevLogs[$num - 1] } elseif ($k) { $log = $k }
    $lk = Read-PevKey '  Levels: E errors and critical, W + warnings, A everything (Enter = W)'
    $levels = switch ($lk.ToLowerInvariant()) { 'e' { @(1, 2) } 'a' { @() } default { @(1, 2, 3) } }
    $search = Read-PevKey '  Text or event ID to look for (Enter = none)'
    if ($script:PevEof) { return }
    Write-Host "  Reading $log..." -ForegroundColor DarkCyan
    try { $items = Get-PevBrowse $log $levels $Days $search }
    catch [UnauthorizedAccessException] { Write-Host "  $log needs an elevated prompt (choose A on the main screen)." -ForegroundColor Yellow; return }
    catch { Write-Host "  Couldn't read $log`: $($_.Exception.Message)" -ForegroundColor Yellow; return }
    Show-PevList "$log - last $Days day(s)$(if ($search) { " - '$search'" }) - $(@($items).Count) event(s), newest first" $items '' ''
}

function Restart-PevElevated {
    $self = $PSCommandPath
    $exe = (Get-Process -Id $PID).Path
    $arg = if ($self) { "-NoExit -ExecutionPolicy Bypass -File `"$self`"" } else { "-NoExit -ExecutionPolicy Bypass -Command `"irm $($script:PevUrl) | iex`"" }
    # the elevated window doesn't inherit this process's environment, so in-memory mode is passed on in the command
    if ($script:PevInMemory) { $arg = if ($self) { "-NoExit -ExecutionPolicy Bypass -Command `"`$env:PXD_INMEMORY='1'; & '$self'`"" } else { "-NoExit -ExecutionPolicy Bypass -Command `"`$env:PXD_INMEMORY='1'; irm $($script:PevUrl) | iex`"" } }
    try { Start-Process -FilePath $exe -ArgumentList $arg -Verb RunAs -ErrorAction Stop; $true } catch { Write-Host '  Not elevated (the prompt was cancelled).' -ForegroundColor Yellow; $false }
}

# ---- PD header ---------------------------------------------------------------------------------------------------
# The PD logo beside the tool's name, shown once when a pxdtek CLI tool starts. The master copy is cli/PdHeader.ps1
# in the pxdtek repo; each tool carries a copy (they run through irm | iex, so they can't load it). Keep them in sync.
# ASCII-only on purpose, so irm | iex never trips on encoding: the solid-block logo is stored as letters
# (U = upper half block, D = lower half block, F = full block) and turned into blocks when it's shown.
# Each run picks one of the two logos at random; a window too narrow for the logo gets a one-line header.
function Show-PdHeader {
    param([string]$Name, [string]$Version = '', [string[]]$About = @(), [string]$Command = '')
    $ascii = @(
        '      -++++++++++-'
        '   :++-          -++:'
        '  ++        +#-     ++'
        ' #-         -+@%-    -#'
        '++    =##*%#%=-=##.   ++'
        '%    +%. .@:+%   *#    %'
        '%    #*   %+:@. .%+    %'
        '++   .##=-=%#%*##=    ++'
        ' #-    -%@+-         -#'
        '  ++     -#+        ++'
        '   :++-          -++:'
        '      -++++++++++-'
    )
    $blocks = @(
        '      DDDUUUUUUDDD'
        '    DUU          UUD'
        '  DU        UFD     UD'
        ' FU         DDFFD    UF'
        'DU    DFUUFFFU UUF    UD'
        'F    DF   F UF   FF    F'
        'F    FF   FD F   FU    F'
        'UD    FDD DFFFDDFU    DU'
        ' FD    UFFUU         DF'
        '  UD     UFD        DU'
        '    UDD          DDU'
        '      UUUDDDDDDUUU'
    )
    $text = New-Object System.Collections.Generic.List[object]
    $text.Add(@($Name, 'White'))
    $text.Add(@(((@($Version, 'pxdtek.com') | Where-Object { $_ }) -join '  -  '), 'DarkGray'))
    if ($About) { $text.Add(@('', 'Gray')); foreach ($a in $About) { $text.Add(@($a, 'Gray')) } }
    if ($Command) { $text.Add(@('', 'Gray')); $text.Add(@($Command, 'DarkCyan')) }
    $textWidth = ($text | ForEach-Object { $_[0].Length } | Measure-Object -Maximum).Maximum

    $w = 0
    try { $w = $Host.UI.RawUI.WindowSize.Width } catch {}
    Write-Host ''
    if ($w -gt 0 -and $w -lt 30 + $textWidth) {
        Write-Host '  (pd) ' -NoNewline -ForegroundColor Cyan
        Write-Host $Name -NoNewline -ForegroundColor White
        Write-Host $(if ($Version) { "  $Version" } else { '' }) -ForegroundColor DarkGray
        Write-Host ''
        return
    }

    if ($null -eq (Get-Variable -Name PdHeaderStyle -Scope Script -ValueOnly -ErrorAction SilentlyContinue)) { $script:PdHeaderStyle = Get-Random -Minimum 0 -Maximum 2 }
    $useBlocks = $script:PdHeaderStyle -eq 1
    $top = [Math]::Max(0, [Math]::Floor(($ascii.Count - $text.Count) / 2))
    for ($i = 0; $i -lt $ascii.Count; $i++) {
        Write-Host '  ' -NoNewline
        if ($useBlocks) {
            $l = $blocks[$i].Replace('U', [string][char]0x2580).Replace('D', [string][char]0x2584).Replace('F', [string][char]0x2588)
            Write-Host $l.PadRight(24) -NoNewline -ForegroundColor Cyan
        } else {
            # dense strokes white, the middle cyan, the edges dark cyan: one colour run at a time
            $run = ''; $col = ''
            foreach ($c in $ascii[$i].PadRight(24).ToCharArray()) {
                $k = if ('@%#'.Contains([string]$c)) { 'White' } elseif ('*+='.Contains([string]$c)) { 'Cyan' } else { 'DarkCyan' }
                if ($k -ne $col -and $run) { Write-Host $run -NoNewline -ForegroundColor $col; $run = '' }
                $col = $k; $run += $c
            }
            if ($run) { Write-Host $run -NoNewline -ForegroundColor $col }
        }
        $j = $i - $top
        if ($j -ge 0 -and $j -lt $text.Count -and $text[$j][0]) { Write-Host ('    ' + $text[$j][0]) -NoNewline -ForegroundColor $text[$j][1] }
        Write-Host ''
    }
    Write-Host ''
}
# ---- end of PD header --------------------------------------------------------------------------------------------

function Start-PevConsole([int]$Days) {
    $snap = $null; $digest = $null
    Show-PdHeader -Name 'pxd Event Viewer' -Version "CLI $($script:PevVersion)" -Command 'irm pxdtek.com/ev|iex'
    if ($script:PevInMemory) { Write-Host '  In-memory: nothing is saved on this PC.' -ForegroundColor DarkGray }
    Write-Host '  Reading the PC snapshot...' -ForegroundColor DarkCyan -NoNewline
    $snap = Get-PevSnapshot
    Write-Host ("`r" + (' ' * 40) + "`r") -NoNewline
    $digest = Get-PevDigest $Days
    while (-not $script:PevEof) {
        Write-Host ''
        Write-Host "  $($snap.ComputerName)" -NoNewline -ForegroundColor White
        Write-Host "  $($snap.OsName) $($snap.OsVersion)  -  up $(Get-PevAgo $snap.Uptime)  -  last $Days day(s)$(if (-not $digest.Admin) { '  -  not elevated' })" -ForegroundColor DarkGray
        Show-PevTiles $digest $snap
        Show-PevDigest $digest
        Write-Host ''
        Write-Host '  [number] open   S snapshot   B browse a log   D days (1/7/30)   T copy for ticket   R save report   F refresh' -ForegroundColor DarkGray
        Write-Host "  $(if (-not $digest.Admin) { 'A restart as administrator   ' })Q quit" -ForegroundColor DarkGray
        $k = Read-PevKey '  Choose'
        switch -Regex ($k.ToLowerInvariant()) {
            '^q$' { return }
            '^s$' { Show-PevSnapshot $snap; [void](Read-PevKey '  Enter to go back'); break }
            '^b$' { Show-PevBrowse $Days; break }
            '^d$' {
                $d = Read-PevKey '  Days to look back (1, 7, 30 or any number up to 365)'
                $v = 0; if ([int]::TryParse($d, [ref]$v) -and $v -ge 1 -and $v -le 365) { $Days = $v; $digest = Get-PevDigest $Days }
                break
            }
            '^f$' { $snap = Get-PevSnapshot; $digest = Get-PevDigest $Days; break }
            '^t$' {
                try { Set-Clipboard -Value (Get-PevTicketText $snap $digest); Write-Host '  Copied: paste it into the ticket.' -ForegroundColor Green }
                catch { Write-Host "  Couldn't use the clipboard here; R saves it as a file instead." -ForegroundColor Yellow }
                break
            }
            '^r$' {
                $default = Join-Path ([Environment]::GetFolderPath('Desktop')) ("pxd-EventViewer-$($snap.ComputerName)-$(Get-Date -Format 'yyyyMMdd-HHmm').html")
                $p = Read-PevKey "  Save as (.html, .txt or .json; Enter = $default)"
                if ($script:PevEof) { break }
                if (-not $p) { $p = $default }
                try { $full = Export-PevReport $p $snap $digest; Write-Host "  Saved $full" -ForegroundColor Green } catch { Write-Host "  Couldn't save: $($_.Exception.Message)" -ForegroundColor Yellow }
                break
            }
            '^a$' { if (-not $digest.Admin -and (Restart-PevElevated)) { Write-Host '  Opened an elevated window; this one can be closed.' -ForegroundColor Green }; break }
            '^\d+$' {
                $n = [int]$k
                if ($n -ge 1 -and $n -le @($script:PevMenu).Count) { $r = $script:PevMenu[$n - 1]; Show-PevList "$($r.Rule.Title) - $($r.Items.Count) event(s)" $r.Items.ToArray() $r.Rule.Explain $r.Rule.Fix }
                break
            }
        }
    }
}

# ======================================================================================
#  Entry
# ======================================================================================

if ($env:OS -ne 'Windows_NT') { Write-Host 'pxd Event Viewer reads Windows event logs, so it runs on Windows only.' -ForegroundColor Yellow; return }

if ($Report -or $Export -or $Ticket) {
    if ($script:PevInMemory -and -not $Export) { Write-Host '  In-memory: nothing is saved on this PC.' -ForegroundColor DarkGray }
    $snap = Get-PevSnapshot
    $digest = Get-PevDigest $Days -Quiet
    if ($Report) { Show-PevSnapshot $snap; Write-Host ''; Show-PevTiles $digest $snap; Show-PevDigest $digest; Write-Host '' }
    if ($Export) { $full = Export-PevReport $Export $snap $digest; Write-Host "  Saved $full" -ForegroundColor Green }
    if ($Ticket) { Set-Clipboard -Value (Get-PevTicketText $snap $digest); Write-Host '  Ticket text copied to the clipboard.' -ForegroundColor Green }
    return
}

Start-PevConsole $Days
