# probe.ps1 - Windows application capability probe
# Usage: powershell -File probe.ps1 <AppName>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$App
)

$OutputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$WinExe = Join-Path $ScriptDir "win.exe"

if (-not (Test-Path $WinExe)) {
    Write-Error "Kernel binary not found. Please build it first: powershell -File $ScriptDir\build.ps1"
    exit 1
}

# 1. Locate executable
$ExePath = $null
$RunningProc = $null

# Check running processes first
$procs = Get-Process | Where-Object {
    $_.ProcessName -like "*$App*" -or $_.MainWindowTitle -like "*$App*"
}
if ($procs) {
    $RunningProc = $procs[0]
    try {
        $ExePath = $RunningProc.Path
    } catch { }
}

# Check Start Menu shortcuts
if (-not $ExePath) {
    $lnkDirs = @(
        "$env:APPDATA\Microsoft\Windows\Start Menu\Programs",
        "$env:ProgramData\Microsoft\Windows\Start Menu\Programs"
    )
    foreach ($dir in $lnkDirs) {
        if (Test-Path $dir) {
            $lnks = Get-ChildItem -Path $dir -Filter "*$App*.lnk" -Recurse -ErrorAction SilentlyContinue
            if ($lnks) {
                try {
                    $sh = New-Object -ComObject WScript.Shell
                    $target = $sh.CreateShortcut($lnks[0].FullName).TargetPath
                    if ($target -and (Test-Path $target)) {
                        $ExePath = $target
                        break
                    }
                } catch { }
            }
        }
    }
}

# Check common install directories
if (-not $ExePath) {
    $searchPaths = @(
        "$env:LOCALAPPDATA\Programs",
        "C:\Program Files",
        "C:\Program Files (x86)"
    )
    foreach ($sp in $searchPaths) {
        if (Test-Path $sp) {
            $found = Get-ChildItem -Path $sp -Filter "*$App*.exe" -Recurse -Depth 3 -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($found) {
                $ExePath = $found.FullName
                break
            }
        }
    }
}

Write-Host "==== Static Capability Probe ====" -ForegroundColor Cyan
if ($ExePath) {
    $item = Get-Item $ExePath
    $ver = $item.VersionInfo.ProductVersion
    if (-not $ver) { $ver = "Unknown" }
    Write-Host "Executable Path: $ExePath"
    Write-Host "Version: $ver"

    # Architecture diagnosis
    $appDir = Split-Path -Parent $ExePath
    $isChromium = $false
    $archDesc = "Native Win32 / Custom Drawing"

    $electronAsar = Test-Path (Join-Path $appDir "resources\app.asar")
    $chromeElf = Test-Path (Join-Path $appDir "chrome_elf.dll")
    $cefDll = Test-Path (Join-Path $appDir "cef.dll")
    $qtCore = Get-ChildItem -Path $appDir -Filter "Qt*Core.dll" -ErrorAction SilentlyContinue
    $flutterDll = Test-Path (Join-Path $appDir "flutter_windows.dll")

    if ($electronAsar -or $chromeElf) {
        $isChromium = $true
        $archDesc = "Electron / Chromium based (Supports --remote-debugging-port for CDP)"
    } elseif ($cefDll) {
        $isChromium = $true
        $archDesc = "CEF (Chromium Embedded Framework) (Can probe CDP port)"
    } elseif ($qtCore) {
        $archDesc = "Qt Custom ($($qtCore[0].Name)) (UIA tree may be partial, fallback to L2 coords)"
    } elseif ($flutterDll) {
        $archDesc = "Flutter Custom (UIA tree empty, use L2 coords)"
    }

    Write-Host "Architecture: $archDesc"
    if ($isChromium) {
        Write-Host "  Recommended route: Launch with --remote-debugging-port and use cdp.js" -ForegroundColor Green
    }
} else {
    Write-Host "Executable not found on disk (If running, probing via process directly)"
}

Write-Host ""
Write-Host "==== Dynamic Runtime Probe ====" -ForegroundColor Cyan
if ($RunningProc) {
    $mainPid = $RunningProc.Id
    Write-Host "Runtime Status: Running (PID: $mainPid, Name: $($RunningProc.ProcessName))"

    # Enumerate listening ports for process and children
    $allPids = @($mainPid)
    $childProcs = Get-CimInstance Win32_Process -Filter "ParentProcessId = $mainPid" -ErrorAction SilentlyContinue
    if ($childProcs) {
        $allPids += ($childProcs | Select-Object -ExpandProperty ProcessId)
    }

    $ports = @()
    foreach ($p in $allPids) {
        $conns = Get-NetTCPConnection -OwningProcess $p -State Listen -ErrorAction SilentlyContinue
        if ($conns) {
            $ports += ($conns | Select-Object -ExpandProperty LocalPort)
        }
    }
    $ports = $ports | Select-Object -Unique

    Write-Host "--- Local Listening Ports (CDP / RPC) ---"
    if ($ports.Count -eq 0) {
        Write-Host "  No listening ports detected"
    } else {
        foreach ($port in $ports) {
            try {
                $resp = Invoke-RestMethod -Uri "http://127.0.0.1:$port/json/version" -TimeoutSec 2 -ErrorAction Stop
                if ($resp.webSocketDebuggerUrl -or $resp.Browser) {
                    Write-Host "  Port $port -> [CDP Detected] Full Chrome DevTools Protocol! Use cdp.js directly." -ForegroundColor Green
                } else {
                    Write-Host "  Port $port -> HTTP service active, no CDP endpoint"
                }
            } catch {
                Write-Host "  Port $port -> Other local port (no response to /json/version)"
            }
        }
    }

    Write-Host ""
    Write-Host "--- Window and UIA Semantic Probe ---"
    & $WinExe windows $RunningProc.ProcessName
    Write-Host ""
    & $WinExe ax $mainPid
} else {
    Write-Host "Application is currently not running. Launch it and re-run probe to inspect runtime channels."
}
