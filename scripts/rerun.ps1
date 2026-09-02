<#
.SYNOPSIS
    Kill any running Akshaya app, clean-build it, and run it again.

.DESCRIPTION
    Native Windows PowerShell equivalent of scripts/rerun.sh and scripts/rerun.py.
    Also runs on PowerShell 7 for macOS / Linux.

.EXAMPLE
    scripts\rerun.ps1                 # clean-build + run API (:5080) and web (:4200), foreground
.EXAMPLE
    scripts\rerun.ps1 -ApiOnly        # backend only
.EXAMPLE
    scripts\rerun.ps1 -WebOnly        # frontend only
.EXAMPLE
    scripts\rerun.ps1 -Detached       # start in the background, log to .run\, exit
.EXAMPLE
    scripts\rerun.ps1 -NoClean        # skip the clean step
.EXAMPLE
    scripts\rerun.ps1 -Reinstall      # also wipe bin/obj and run npm ci
.EXAMPLE
    scripts\rerun.ps1 -Relaxed        # build with TreatWarningsAsErrors/NuGetAudit off
.EXAMPLE
    scripts\rerun.ps1 -Kill           # only kill what is running, then stop
#>
[CmdletBinding()]
param(
    [switch]$ApiOnly,
    [switch]$WebOnly,
    [Alias('d')][switch]$Detached,
    [switch]$NoClean,
    [switch]$Reinstall,
    [switch]$Relaxed,
    [switch]$Kill
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$ApiProject = Join-Path $RepoRoot 'src/Akshaya.Api'
$ApiPort    = 5080
$ApiUrl     = "http://localhost:$ApiPort"
$WebDir     = Join-Path $RepoRoot 'apps/web'
$WebPort    = 4200
$RunDir     = Join-Path $RepoRoot '.run'

if ($ApiOnly -and $WebOnly) { throw '-ApiOnly and -WebOnly are mutually exclusive' }
$ScopeApi = -not $WebOnly
$ScopeWeb = -not $ApiOnly
$RelaxedProps = if ($Relaxed) { @('-p:TreatWarningsAsErrors=false', '-p:NuGetAudit=false') } else { @() }

function Say  ($m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Warn ($m) { Write-Host " warn $m" -ForegroundColor Yellow }
function Die  ($m) { Write-Host "fatal $m" -ForegroundColor Red; exit 1 }

function Get-PidsOnPort ($Port) {
    try {
        return (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction Stop |
                Select-Object -ExpandProperty OwningProcess -Unique)
    } catch {
        $out = netstat -ano -p tcp | Select-String ":$Port\s" | Select-String 'LISTENING'
        return ($out | ForEach-Object { ($_ -split '\s+')[-1] } | Sort-Object -Unique)
    }
}

function Stop-Pid ($ProcId) {
    try { Stop-Process -Id $ProcId -Force -ErrorAction SilentlyContinue } catch {}
}

function Stop-Recorded {
    if (-not (Test-Path $RunDir)) { return }
    Get-ChildItem -Path $RunDir -Filter *.pid -ErrorAction SilentlyContinue | ForEach-Object {
        $p = (Get-Content $_.FullName -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($p) { Stop-Pid ([int]$p) }
        Remove-Item $_.FullName -ErrorAction SilentlyContinue
    }
}

function Stop-ByCommandLine ($Needle) {
    Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine -like "*$Needle*" } |
        ForEach-Object { Stop-Pid $_.ProcessId }
}

function Kill-Everything {
    Say 'Stopping anything already running'
    Stop-Recorded
    $ports = @()
    if ($ScopeApi) { $ports += $ApiPort }
    if ($ScopeWeb) { $ports += $WebPort }
    foreach ($port in $ports) {
        foreach ($procId in Get-PidsOnPort $port) {
            if ($procId) { Warn "port $port held by pid $procId - killing"; Stop-Pid ([int]$procId) }
        }
    }
    if ($ScopeApi) { Stop-ByCommandLine 'Akshaya.Api' }
    if ($ScopeWeb) { Stop-ByCommandLine 'ng serve'; Stop-ByCommandLine 'angular/cli' }
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline -and ($ports | ForEach-Object { Get-PidsOnPort $_ })) {
        Start-Sleep -Milliseconds 250
    }
}

function Clean-Step {
    Say 'Cleaning'
    if ($ScopeApi) {
        & dotnet clean $ApiProject -v quiet --nologo 2>&1 | Out-Null
        if ($Reinstall) {
            foreach ($root in 'src', 'tests') {
                Get-ChildItem -Path (Join-Path $RepoRoot $root) -Recurse -Directory `
                    -Include bin, obj -ErrorAction SilentlyContinue |
                    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
    if ($ScopeWeb) {
        foreach ($sub in '.angular', 'dist') {
            Remove-Item (Join-Path $WebDir $sub) -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Build-Step {
    if ($ScopeApi) {
        Say "dotnet build src/Akshaya.Api"
        & dotnet build $ApiProject -c Debug --nologo @RelaxedProps
        if ($LASTEXITCODE -ne 0) { Die "dotnet build exited $LASTEXITCODE" }
    }
    if ($ScopeWeb) {
        $nodeModules = Join-Path $WebDir 'node_modules'
        if ($Reinstall -or -not (Test-Path $nodeModules)) {
            $verb = if (Test-Path (Join-Path $WebDir 'package-lock.json')) { 'ci' } else { 'install' }
            Push-Location $WebDir
            try { & npm $verb; if ($LASTEXITCODE -ne 0) { Die "npm $verb exited $LASTEXITCODE" } }
            finally { Pop-Location }
        } else {
            Say 'web deps present - skipping npm ci (use -Reinstall to force)'
        }
    }
}

function Wait-ForPort ($Port, $TimeoutSeconds = 60) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $c = New-Object Net.Sockets.TcpClient
            $c.Connect('127.0.0.1', $Port); $c.Close(); return $true
        } catch { Start-Sleep -Milliseconds 500 }
    }
    return $false
}

$ApiArgs = @('run', '--project', $ApiProject, '-c', 'Debug', '--no-build') + $RelaxedProps
$WebArgs = @('start', '--', '--port', "$WebPort")

function Start-One ($File, $Arguments, $WorkDir, $LogFile) {
    $params = @{ FilePath = $File; ArgumentList = $Arguments; WorkingDirectory = $WorkDir; PassThru = $true }
    if ($LogFile) {
        $params.RedirectStandardOutput = $LogFile
        $params.RedirectStandardError  = "$LogFile.err"
        $params.NoNewWindow = $true
    } else {
        $params.NoNewWindow = $true
    }
    return Start-Process @params
}

function Run-Foreground {
    $procs = @()
    $stop = {
        Say 'Shutting down'
        foreach ($p in $procs) { if ($p -and -not $p.HasExited) { Stop-Pid $p.Id } }
    }
    try {
        if ($ScopeApi) {
            Say "Starting API on $ApiUrl"
            $env:ASPNETCORE_URLS = $ApiUrl
            if (-not $env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }
            $procs += Start-One 'dotnet' $ApiArgs $RepoRoot $null
            if ($ScopeWeb) {
                Say 'Waiting for the API to accept connections'
                if (-not (Wait-ForPort $ApiPort)) { Warn 'API did not open its port in time - starting web anyway' }
            }
        }
        if ($ScopeWeb) {
            Say "Starting web on http://localhost:$WebPort"
            $procs += Start-One 'npm' $WebArgs $WebDir $null
        }
        while ($true) {
            foreach ($p in $procs) {
                if ($p.HasExited) { Warn "a process exited with $($p.ExitCode) - stopping the rest"; & $stop; return $p.ExitCode }
            }
            Start-Sleep -Milliseconds 500
        }
    } finally {
        & $stop
    }
}

function Run-Detached {
    New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
    if ($ScopeApi) {
        $env:ASPNETCORE_URLS = $ApiUrl
        if (-not $env:ASPNETCORE_ENVIRONMENT) { $env:ASPNETCORE_ENVIRONMENT = 'Development' }
        $p = Start-One 'dotnet' $ApiArgs $RepoRoot (Join-Path $RunDir 'api.log')
        $p.Id | Out-File (Join-Path $RunDir 'api.pid') -Encoding ascii
        Say "API  $ApiUrl  (pid $($p.Id))  logs: .run\api.log"
    }
    if ($ScopeWeb) {
        $p = Start-One 'npm' $WebArgs $WebDir (Join-Path $RunDir 'web.log')
        $p.Id | Out-File (Join-Path $RunDir 'web.pid') -Encoding ascii
        Say "web  http://localhost:$WebPort  (pid $($p.Id))  logs: .run\web.log"
    }
    Write-Host ''
    Say 'Tail:  Get-Content .run\*.log -Wait'
    Say 'Stop:  scripts\rerun.ps1 -Kill'
}

# -- go ------------------------------------------------------------------------
if ($ScopeApi -and -not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Die 'dotnet not found on PATH' }
if ($ScopeWeb -and -not (Get-Command npm    -ErrorAction SilentlyContinue)) { Die 'npm not found on PATH' }

Kill-Everything
if ($Kill) { Say 'Done (kill only)'; exit 0 }

if (-not $NoClean) { Clean-Step }
Build-Step

if ($Detached) { Run-Detached } else { exit (Run-Foreground) }
