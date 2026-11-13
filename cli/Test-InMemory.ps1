<#
    Checks the console edition's in-memory mode (PXD_INMEMORY=1, set by the pxd Toolkit console): it writes nothing on
    the PC except a report the user asks for, saved where the user says. APPDATA, LOCALAPPDATA, ProgramData and
    TEMP/TMP point at a temp folder for this process, and every file under it is compared before and after.
    Keys are fed to the console; nothing is shown on screen and no other window is started.
    Run from Windows PowerShell or pwsh:  powershell -ExecutionPolicy Bypass -File cli\Test-InMemory.ps1
#>
$t = Join-Path ([IO.Path]::GetTempPath()) ('pev-inmemory-test-' + [guid]::NewGuid().ToString('N'))
$saved = @{}
foreach ($n in 'APPDATA', 'LOCALAPPDATA', 'ProgramData', 'TEMP', 'TMP', 'PXD_INMEMORY') { $saved[$n] = [Environment]::GetEnvironmentVariable($n) }
$watch = @{ APPDATA = (Join-Path $t 'AppData'); LOCALAPPDATA = (Join-Path $t 'LocalAppData'); ProgramData = (Join-Path $t 'ProgramData'); TEMP = (Join-Path $t 'Temp') }
foreach ($d in $watch.Values) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
$picked = Join-Path $t 'Picked'   # where the "user" asks for reports (not watched)
New-Item -ItemType Directory -Force -Path $picked | Out-Null

$fails = 0
function Check([string]$Name, [bool]$Ok) { if ($Ok) { Microsoft.PowerShell.Utility\Write-Host "PASS  $Name" -ForegroundColor Green } else { Microsoft.PowerShell.Utility\Write-Host "FAIL  $Name" -ForegroundColor Red; $script:fails++ } }
function Get-Files { @(Get-ChildItem -LiteralPath @($watch.Values) -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }) }
function Same($a, $b) { (@($a) -join '|') -eq (@($b) -join '|') }

try {
    $env:APPDATA = $watch.APPDATA; $env:LOCALAPPDATA = $watch.LOCALAPPDATA; $env:ProgramData = $watch.ProgramData
    $env:TEMP = $watch.TEMP; $env:TMP = $watch.TEMP
    $env:PXD_INMEMORY = '1'
    $src = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'pxdEventViewer.ps1'))
    $sb = [scriptblock]::Create($src)

    # Console output is captured, keys come from a queue, and nothing is started or put on the clipboard
    $script:Out = New-Object System.Text.StringBuilder
    function Write-Host { param([Parameter(Position = 0)]$Object = '', [switch]$NoNewline, $ForegroundColor, $BackgroundColor) [void]$script:Out.Append([string]$Object); if (-not $NoNewline) { [void]$script:Out.AppendLine() } }
    function Clear-Host { }
    $script:Keys = New-Object System.Collections.Queue
    function Read-Host { param([Parameter(Position = 0)]$Prompt) if ($script:Keys.Count) { return [string]$script:Keys.Dequeue() } return $null }
    $script:Started = @()
    function Start-Process { param($FilePath, $ArgumentList, $Verb, $ErrorAction) $script:Started += , @($FilePath, [string]$ArgumentList, $Verb) }
    function Set-Clipboard { param($Value) $script:Clip = $Value }

    $before = Get-Files

    # ---- 1) interactive: snapshot, ticket text, a report the user asks for, restart as administrator, quit
    $report = Join-Path $picked 'asked-for.html'
    foreach ($k in 's', '', 't', 'r', $report, 'a', 'q') { $script:Keys.Enqueue($k) }
    & $sb
    $o = $script:Out.ToString()
    Check 'console: in-memory line shown' ($o -match 'In-memory: nothing is saved on this PC\.')
    Check 'console: the report the user asked for is saved where asked' (Test-Path -LiteralPath $report)
    Check 'console: ticket text went to the clipboard only' ([string]$script:Clip -match 'Problems in the last')
    $elev = @($script:Started | Where-Object { $_[2] -eq 'RunAs' })
    Check 'console: elevated window keeps in-memory mode' ($elev.Count -eq 1 -and $elev[0][1] -match [regex]::Escape("`$env:PXD_INMEMORY='1'"))
    Check 'console: nothing else written' (Same $before (Get-Files))

    # ---- 2) non-interactive: -Report prints only; -Export writes only the asked-for file
    [void]$script:Out.Clear()
    & $sb -Report -Days 1
    Check '-Report: in-memory line shown' ($script:Out.ToString() -match 'In-memory: nothing is saved on this PC\.')
    $json = Join-Path $picked 'asked-for.json'
    & $sb -Export $json -Days 1
    Check '-Export: the asked-for file is written' (Test-Path -LiteralPath $json)
    Check '-Report / -Export: nothing else written' (Same $before (Get-Files))
    Check 'only the two asked-for files exist outside the watched folders' (@(Get-ChildItem -LiteralPath $picked).Count -eq 2)

    # ---- 3) the elevated window's command when run through irm | iex: pxdtek.com first, GitHub fallback, never a block page
    #      (the command is run here with a fake Invoke-RestMethod; nothing goes to the network)
    . ([scriptblock]::Create($src.Substring(0, $src.LastIndexOf('#  Entry'))))
    $cmd = Get-PevRelaunchCommand ''
    Check 'irm | iex relaunch: in-memory kept, pxdtek.com first, GitHub fallback' ($cmd.StartsWith("`$env:PXD_INMEMORY='1'; ") -and $cmd.IndexOf('https://pxdtek.com/ev') -ge 0 -and $cmd.IndexOf('https://pxdtek.com/ev') -lt $cmd.IndexOf('https://raw.githubusercontent.com/pxdtek/pxdEventViewer/HEAD/cli/pxdEventViewer.ps1'))
    $block = '<!DOCTYPE html><html><body>This site is blocked by your organization.</body></html>'
    $good = 'function Start-PevConsole { }; $script:RelaunchRan = $true'
    function Invoke-RestMethod { param([Parameter(Position = 0)]$Uri) $script:Fetched += , $Uri; $a = $script:Answers[$script:Fetched.Count - 1]; if ($a -eq 'fail') { throw 'offline' }; $a }
    foreach ($case in @(
            @{ Name = 'pxdtek.com answers'; Answers = @($good); Ran = $true; Fetches = 1 },
            @{ Name = 'pxdtek.com gives a block page: GitHub copy runs'; Answers = @($block, $good); Ran = $true; Fetches = 2 },
            @{ Name = 'pxdtek.com fails: GitHub copy runs'; Answers = @('fail', $good); Ran = $true; Fetches = 2 },
            @{ Name = 'both give block pages: nothing runs'; Answers = @($block, $block); Ran = $false; Fetches = 2 })) {
        $script:Fetched = @(); $script:Answers = $case.Answers; $script:RelaunchRan = $false
        $saveMem = $env:PXD_INMEMORY
        & ([scriptblock]::Create($cmd))
        $env:PXD_INMEMORY = $saveMem
        Check "relaunch: $($case.Name)" ([bool]$script:RelaunchRan -eq $case.Ran -and $script:Fetched.Count -eq $case.Fetches)
    }
    Remove-Item Function:\Invoke-RestMethod

    # ---- 4) control: the file check does see a write in a watched folder
    Set-Content -LiteralPath (Join-Path $watch.TEMP 'control.txt') -Value 'x'
    Check 'control: a write is detected' (-not (Same $before (Get-Files)))
} catch {
    Check "no unexpected error ($($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber))" $false
} finally {
    foreach ($n in $saved.Keys) { [Environment]::SetEnvironmentVariable($n, $saved[$n]) }
    if ($t -and (Split-Path $t -Leaf) -like 'pev-inmemory-test-*' -and (Test-Path -LiteralPath $t)) { Remove-Item -LiteralPath $t -Recurse -Force }
}
if ($fails) { Microsoft.PowerShell.Utility\Write-Host "$fails check(s) failed" -ForegroundColor Red; exit 1 } else { Microsoft.PowerShell.Utility\Write-Host 'All in-memory checks passed' -ForegroundColor Green }
