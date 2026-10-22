# build.ps1 - 编译 huashu-win-use 操控内核
$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$WinProj = Join-Path $ScriptDir "win\win.csproj"
$BinDir = Join-Path $ScriptDir "bin"
$TargetExe = Join-Path $ScriptDir "win.exe"

Write-Host "Building huashu-win-use core (.NET 9 win-x64)..." -ForegroundColor Cyan

# Use dotnet publish to output a standalone win.exe into scripts/
dotnet publish $WinProj -c Release -o $BinDir -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

$PublishedExe = Join-Path $BinDir "win.exe"
if (Test-Path $PublishedExe) {
    Copy-Item $PublishedExe $TargetExe -Force
    Write-Host "内核编译完成: $TargetExe" -ForegroundColor Green
} else {
    Write-Error "编译失败：未找到产物 $PublishedExe"
}
