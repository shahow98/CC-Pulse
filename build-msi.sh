#!/usr/bin/env bash
# Build CC-Pulse MSI installer
# Prerequisites: .NET 8 SDK, WiX v5 (dotnet tool install --global wix --version 5.0.2)

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

PUBLISH_DIR="ClaudeMonitor/bin/Release/net8.0-windows/win-x64/publish"
HOOK_PROXY_PUBLISH_DIR="ClaudeMonitor.HookProxy/bin/Release/net8.0-windows/win-x64/publish"
HOOKS_DIR="ClaudeMonitor/Hooks"
SOURCE_DIR="ClaudeMonitor/"
MSI_OUT="Installer/CC-Pulse.msi"

echo "=== Building CC-Pulse MSI Installer ==="

# Step 1: Publish the application
echo "[1/3] Publishing ClaudeMonitor..."
dotnet publish ClaudeMonitor/ClaudeMonitor.csproj -r win-x64 -c Release

# Step 2: Publish the hook proxy (console-mode exe for reliable stdin)
echo "[2/3] Publishing CC-Pulse-Hook..."
dotnet publish ClaudeMonitor.HookProxy/ClaudeMonitor.HookProxy.csproj -r win-x64 -c Release

# Step 3: Build the MSI
echo "[3/3] Building MSI..."

# Copy License.rtf to root so WixVariable can find it (it resolves relative to working dir)
cp Installer/License.rtf License.rtf

wix build \
  -arch x64 \
  -d "PublishDir=$PUBLISH_DIR/" \
  -d "HookProxyPublishDir=$HOOK_PROXY_PUBLISH_DIR/" \
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
