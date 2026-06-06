#!/usr/bin/env bash
#
# Generates MeltdownMonitorWidget.xcodeproj from project.yml and, with --build,
# compiles the Live Activity bridge framework and the widget extension .appex
# that the .NET app links and embeds. Mac-only (needs Xcode + XcodeGen).
#
#   ./generate.sh            # just (re)generate the .xcodeproj
#   ./generate.sh --build    # generate, then xcodebuild the framework + .appex
#
# Outputs (when --build) land in build/<config>-<sdk>/ next to this script:
#   MeltdownLiveActivityBridge.framework   <- <NativeReference> from the .csproj
#   MeltdownMonitorWidgetExtension.appex   <- copied into the app's PlugIns/
#
# See docs/live-activity.md ▸ "Reproducible Xcode wiring" for how these attach
# to the .NET-built app.
set -euo pipefail

cd "$(dirname "$0")"

if ! command -v xcodegen >/dev/null 2>&1; then
  echo "error: xcodegen not found. Install it with:  brew install xcodegen" >&2
  exit 1
fi

echo "==> xcodegen generate"
xcodegen generate --spec project.yml

if [[ "${1:-}" != "--build" ]]; then
  echo "Generated MeltdownMonitorWidget.xcodeproj. Re-run with --build to compile."
  exit 0
fi

if ! command -v xcodebuild >/dev/null 2>&1; then
  echo "error: xcodebuild not found (Xcode command line tools required)." >&2
  exit 1
fi

# Default to a device build; override with SDK=iphonesimulator for the simulator.
SDK="${SDK:-iphoneos}"
CONFIG="${CONFIG:-Release}"
OUT="build/${CONFIG}-${SDK}"
mkdir -p "$OUT"

for target in MeltdownLiveActivityBridge MeltdownMonitorWidgetExtension; do
  echo "==> xcodebuild $target ($CONFIG / $SDK)"
  xcodebuild \
    -project MeltdownMonitorWidget.xcodeproj \
    -target "$target" \
    -configuration "$CONFIG" \
    -sdk "$SDK" \
    CONFIGURATION_BUILD_DIR="$(pwd)/$OUT" \
    build
done

echo
echo "Built into $OUT:"
ls -1 "$OUT" | sed 's/^/  /'
