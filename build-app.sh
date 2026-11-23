#!/bin/bash
# Builds build/TricksterQuestSender.app so macOS remembers the Accessibility permission.
set -euo pipefail
cd "$(dirname "$0")"

APP_NAME="TricksterQuestSender"
APP="build/$APP_NAME.app"

swift build -c release
BIN="$(swift build -c release --show-bin-path)/$APP_NAME"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp "$BIN" "$APP/Contents/MacOS/$APP_NAME"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>$APP_NAME</string>
    <key>CFBundleIdentifier</key><string>com.piyawatp.trickster-quest-sender</string>
    <key>CFBundleName</key><string>Trickster Quest Sender</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

codesign --force --deep --sign - "$APP"
echo "Built: $APP"
echo "Run:   open \"$APP\""
