#!/usr/bin/env bash
# Build CC-Pulse MSI installer
# Prerequisites: .NET 8 SDK, WiX v5 (dotnet tool install --global wix --version 5.0.2)

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

PUBLISH_DIR="ClaudeMonitor/bin/Release/net8.0-windows/win-x64/publish"
HOOKS_DIR="ClaudeMonitor/Hooks"
SOURCE_DIR="ClaudeMonitor/"
MSI_OUT="Installer/CC-Pulse.msi"

echo "=== Building CC-Pulse MSI Installer ==="

# Step 1: Publish the application
echo "[1/2] Publishing ClaudeMonitor..."
dotnet publish ClaudeMonitor/ClaudeMonitor.csproj -r win-x64 -c Release

# Step 2: Build the MSI
echo "[2/2] Building MSI..."
wix build \
  -arch x64 \
  -d "PublishDir=$PUBLISH_DIR/" \
  -d "HooksDir=$HOOKS_DIR/" \
  -d "SourceDir=$SOURCE_DIR" \
  -ext WixToolset.UI.wixext \
  -ext WixToolset.Util.wixext \
  -out "$MSI_OUT" \
  Installer/CC-Pulse.wxs

echo ""
echo "=== Build complete ==="
echo "MSI: $MSI_OUT"
ls -lh "$MSI_OUT"
