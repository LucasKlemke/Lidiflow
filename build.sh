#!/bin/bash
# Builds Lidiflow.app into ./dist. Pass --install to copy it to /Applications,
# or --release to build for Intel + Apple Silicon and package it as Lidiflow-x.y.z.zip and Lidiflow.dmg.
#
# With a "Developer ID Application" certificate in the Keychain, the app is signed with it, and
# --release also notarizes it (keychain profile "lidiflow", see `xcrun notarytool store-credentials`),
# so it opens without the Gatekeeper warning. Without one, it's signed ad-hoc.
set -euo pipefail
cd "$(dirname "$0")"

IDENTITY=$(security find-identity -v -p codesigning | sed -n 's/.*"\(Developer ID Application: [^"]*\)".*/\1/p' | head -1)
NOTARY_PROFILE=lidiflow

# Signs with the Developer ID (hardened runtime, as notarization requires) or ad-hoc.
sign() {
    if [[ -n "$IDENTITY" ]]; then
        codesign --force --timestamp --options runtime --sign "$IDENTITY" "$@"
    else
        codesign --force --sign - "$@"
    fi
}

# Uploads to Apple's notary service, waits for the result and attaches the ticket.
notarize() {
    xcrun notarytool submit "$1" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$1"
}

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
sign "$APP/Contents/Frameworks/CTranscribe.framework"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp -R Resources/ProviderIcons "$APP/Contents/Resources/ProviderIcons"

ICONSET="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$ICONSET"
for size in 16 32 128 256 512; do
    sips -z $size $size Resources/AppIcon-1024.png --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) Resources/AppIcon-1024.png --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
if [[ -n "$IDENTITY" ]]; then
    # The Developer ID requirement (bundle id + team) already survives rebuilds.
    sign --entitlements Resources/Lidiflow.entitlements "$APP"
    echo "Built $APP (signed: $IDENTITY)"
else
    # Pin the designated requirement to the bundle id so macOS keeps the
    # Accessibility/Microphone grants across rebuilds (ad-hoc defaults to the cdhash).
    codesign --force --sign - -r='designated => identifier "com.lidiflow.app"' "$APP"
    echo "Built $APP (ad-hoc)"
fi

if [[ "${1:-}" == "--install" ]]; then
    rm -rf /Applications/Lidiflow.app
    cp -R "$APP" /Applications/
    echo "Installed /Applications/Lidiflow.app"
fi

if [[ "${1:-}" == "--release" ]]; then
    VERSION=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" "$APP/Contents/Info.plist")

    # Drag-to-Applications disk image. create-dmg drives Finder via AppleScript,
    # so it needs a logged-in session (not a headless CI runner).
    command -v create-dmg >/dev/null || { echo "create-dmg not found: brew install create-dmg" >&2; exit 1; }
    # Unversioned, so releases/latest/download/Lidiflow.dmg always points at the newest build.
    DMG="dist/Lidiflow.dmg"
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

    if [[ -n "$IDENTITY" ]]; then
        sign "$DMG"
        # Notarizing the disk image covers the app inside it too, so both get a ticket.
        notarize "$DMG"
        xcrun stapler staple "$APP"
    else
        echo "Warning: no Developer ID certificate, so the release isn't notarized." >&2
    fi
    echo "Packaged $DMG"

    ZIP="dist/Lidiflow-$VERSION.zip"
    rm -f "$ZIP"
    ditto -c -k --keepParent "$APP" "$ZIP"
    echo "Packaged $ZIP"
fi
