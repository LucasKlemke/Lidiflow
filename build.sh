#!/bin/bash
# Builds Lidiflow.app into ./dist. Pass --install to copy it to /Applications,
# or --release to build for Intel + Apple Silicon and zip it for sharing.
set -euo pipefail
cd "$(dirname "$0")"

ARCHS=()
[[ "${1:-}" == "--release" ]] && ARCHS=(--arch arm64 --arch x86_64)
swift build -c release "${ARCHS[@]}"
APP=dist/Lidiflow.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$(swift build -c release "${ARCHS[@]}" --show-bin-path)/Lidiflow" "$APP/Contents/MacOS/Lidiflow"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp -R Resources/ProviderIcons "$APP/Contents/Resources/ProviderIcons"

ICONSET="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z $size $size Resources/AppIcon-1024.png --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) Resources/AppIcon-1024.png --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
# Pin the designated requirement to the bundle id so macOS keeps the
# Accessibility/Microphone grants across rebuilds (ad-hoc defaults to the cdhash).
codesign --force --sign - -r='designated => identifier "com.lidiflow.app"' "$APP"
echo "Built $APP"

if [[ "${1:-}" == "--install" ]]; then
    rm -rf /Applications/Lidiflow.app
    cp -R "$APP" /Applications/
    echo "Installed /Applications/Lidiflow.app"
fi

if [[ "${1:-}" == "--release" ]]; then
    VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" "$APP/Contents/Info.plist")
    ZIP="dist/Lidiflow-$VERSION.zip"
    rm -f "$ZIP"
    ditto -c -k --keepParent "$APP" "$ZIP"
    echo "Packaged $ZIP"
fi
