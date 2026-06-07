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
BUILD_DIR="$(pwd)/$OUT"

PROJ=(-project MeltdownMonitorWidget.xcodeproj -configuration "$CONFIG" -sdk "$SDK")
DEST=(CONFIGURATION_BUILD_DIR="$BUILD_DIR")
UNSIGNED=(CODE_SIGNING_ALLOWED=NO CODE_SIGNING_REQUIRED=NO CODE_SIGN_IDENTITY= CODE_SIGN_ENTITLEMENTS=)

# Three signing modes, picked from the environment:
#   * MM_APPEX_PROFILE set  -> RELEASE/TestFlight. The .appex is distribution-signed
#     with its own provisioning profile (MM_APPEX_PROFILE = profile name,
#     MM_APPEX_IDENTITY = signing identity), and the .NET app must leave it intact
#     (SkipCodesignItems in the .csproj). The bridge framework is built UNSIGNED —
#     embedded frameworks need no profile and are signed by the app's codesign pass.
#   * DEVELOPMENT_TEAM set   -> local on-device dev: Xcode automatic signing.
#   * neither                -> unsigned (CI build-test, quick local checks).
if [[ -n "${MM_APPEX_PROFILE:-}" ]]; then
  : "${MM_APPEX_IDENTITY:?MM_APPEX_PROFILE set but MM_APPEX_IDENTITY missing}"
  echo "==> Release signing: framework unsigned, .appex signed ($MM_APPEX_IDENTITY / $MM_APPEX_PROFILE)"
  xcodebuild "${PROJ[@]}" -target MeltdownLiveActivityBridge "${UNSIGNED[@]}" "${DEST[@]}" build
  xcodebuild "${PROJ[@]}" -target MeltdownMonitorWidgetExtension \
    CODE_SIGN_STYLE=Manual \
    DEVELOPMENT_TEAM="${DEVELOPMENT_TEAM:-}" \
    CODE_SIGN_IDENTITY="$MM_APPEX_IDENTITY" \
    PROVISIONING_PROFILE_SPECIFIER="$MM_APPEX_PROFILE" \
    "${DEST[@]}" build
elif [[ -z "${DEVELOPMENT_TEAM:-}" ]]; then
  echo "==> DEVELOPMENT_TEAM unset — building unsigned"
  for target in MeltdownLiveActivityBridge MeltdownMonitorWidgetExtension; do
    echo "==> xcodebuild $target ($CONFIG / $SDK)"
    xcodebuild "${PROJ[@]}" -target "$target" "${UNSIGNED[@]}" "${DEST[@]}" build
  done
else
  echo "==> Automatic signing (DEVELOPMENT_TEAM=$DEVELOPMENT_TEAM)"
  for target in MeltdownLiveActivityBridge MeltdownMonitorWidgetExtension; do
    echo "==> xcodebuild $target ($CONFIG / $SDK)"
    xcodebuild "${PROJ[@]}" -target "$target" -allowProvisioningUpdates \
      DEVELOPMENT_TEAM="$DEVELOPMENT_TEAM" "${DEST[@]}" build
  done
fi

echo
echo "Built into $OUT:"
ls -1 "$OUT" | sed 's/^/  /'
