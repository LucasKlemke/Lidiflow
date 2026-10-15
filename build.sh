#!/bin/bash
# Builds Lidiflow.app into ./dist. Pass --install to copy it to /Applications,
# or --release to build for Intel + Apple Silicon and package it as .zip and .dmg.
set -euo pipefail
cd "$(dirname "$0")"

ARCHS=()
[[ "${1:-}" == "--release" ]] && ARCHS=(--arch arm64 --arch x86_64)
swift build -c release ${ARCHS[@]+"${ARCHS[@]}"}
APP=dist/Lidiflow.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources" "$APP/Contents/Frameworks"
BIN="$(swift build -c release ${ARCHS[@]+"${ARCHS[@]}"} --show-bin-path)"
cp "$BIN/Lidiflow" "$APP/Contents/MacOS/Lidiflow"
# transcribe.cpp (Nemotron and other GGUF models), loaded via @executable_path/../Frameworks.
ditto "$BIN/CTranscribe.framework" "$APP/Contents/Frameworks/CTranscribe.framework"
codesign --force --sign - "$APP/Contents/Frameworks/CTranscribe.framework"
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

    # Drag-to-Applications disk image. create-dmg drives Finder via AppleScript,
    # so it needs a logged-in session (not a headless CI runner).
    command -v create-dmg >/dev/null || { echo "create-dmg not found: brew install create-dmg" >&2; exit 1; }
    DMG="dist/Lidiflow-$VERSION.dmg"
    STAGE="$(mktemp -d)"
    cp -R "$APP" "$STAGE/"
    rm -f "$DMG"
    create-dmg \
        --volname "Lidiflow" \
        --volicon "$APP/Contents/Resources/AppIcon.icns" \
        --background Resources/dmg-background.tiff \
        --window-size 660 400 \
        --icon-size 112 \
        --icon "Lidiflow.app" 180 190 \
        --hide-extension "Lidiflow.app" \
        --app-drop-link 480 190 \
        "$DMG" "$STAGE" >/dev/null
    rm -rf "$STAGE"
    echo "Packaged $DMG"
fi
