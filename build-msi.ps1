# Build CC-Pulse MSI installer
# Prerequisites: .NET 8 SDK, WiX v5 (dotnet tool install --global wix --version 5.0.2)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

$PublishDir = "ClaudeMonitor\bin\Release\net8.0-windows\win-x64\publish"
$HookProxyPublishDir = "ClaudeMonitor.HookProxy\bin\Release\net8.0-windows\win-x64\publish"
$HooksDir   = "ClaudeMonitor\Hooks"
$SourceDir  = "ClaudeMonitor\"
$MsiOut     = "Installer\CC-Pulse.msi"

# Locate wix.exe — prefer PATH, then dotnet global tools, then known locations
$WixExe = $null
foreach ($candidate in @(
    (Get-Command wix -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    "$env:USERPROFILE\.dotnet\tools\wix.exe",
    "C:\Program Files\WiX Toolset v7.0\bin\wix.exe"
)) {
    if ($candidate -and (Test-Path $candidate)) {
        $WixExe = $candidate
        break
    }
}
if (-not $WixExe) {
    Write-Error "wix.exe not found. Install WiX v5: dotnet tool install --global wix --version 5.0.2"
    exit 1
}

Write-Host "=== Building CC-Pulse MSI Installer ===" -ForegroundColor Cyan
Write-Host "Using wix: $WixExe"

# Step 1: Publish the application
Write-Host "[1/3] Publishing ClaudeMonitor..." -ForegroundColor Yellow
dotnet publish ClaudeMonitor\ClaudeMonitor.csproj -r win-x64 -c Release

# Step 2: Publish the hook proxy
Write-Host "[2/3] Publishing HookProxy..." -ForegroundColor Yellow
dotnet publish ClaudeMonitor.HookProxy\ClaudeMonitor.HookProxy.csproj -r win-x64 -c Release

# Step 3: Build the MSI
Write-Host "[3/3] Building MSI..." -ForegroundColor Yellow

# Copy License.rtf to root so WixVariable can find it (it resolves relative to working dir)
Copy-Item "Installer\License.rtf" "License.rtf" -Force

& $WixExe build `
  -arch x64 `
  -d "PublishDir=$PublishDir\" `
  -d "HookProxyPublishDir=$HookProxyPublishDir\" `
  -d "HooksDir=$HooksDir\" `
  -d "SourceDir=$SourceDir" `
  -ext WixToolset.UI.wixext `
  -ext WixToolset.Util.wixext `
  -out $MsiOut `
  Installer\CC-Pulse.wxs

Write-Host ""
Write-Host "=== Build complete ===" -ForegroundColor Green
Write-Host "MSI: $MsiOut"
Get-Item $MsiOut | Format-List Name, Length, LastWriteTime
