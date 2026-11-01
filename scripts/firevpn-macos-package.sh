#!/bin/bash
# FireVPN для macOS: собрать FireVPN.app и установочный FireVPN-<версия>-macos-<arch>.dmg.
# Запускается в GitHub Actions на macOS (.github/workflows/firevpn-macos.yml):
#   scripts/firevpn-macos-package.sh <arm64|64> <папка dotnet publish> <версия>
set -euo pipefail

Arch="$1"            # arm64 — Apple M1…M4, 64 — Intel
OutputPath="$2"
Version="$3"

# ядра Xray и sing-box для macOS — из официального набора v2rayN
CoreZip="v2rayN-macos-${Arch}.zip"
curl -fsSL -o "$CoreZip" "https://github.com/2dust/v2rayN-core-bin/raw/refs/heads/master/$CoreZip"
7z x -y "$CoreZip" >/dev/null
cp -rf "v2rayN-macos-${Arch}/"* "$OutputPath"
rm -rf "$OutputPath/bin/mihomo"   # ядро mihomo FireVPN не нужно

App="FireVPN-Package-${Arch}/FireVPN.app"
rm -rf "FireVPN-Package-${Arch}"
mkdir -p "$App/Contents/Resources"
cp -rf "$OutputPath" "$App/Contents/MacOS"
cp -f "$App/Contents/MacOS/v2rayN.icns" "$App/Contents/Resources/AppIcon.icns"
# настройки — в ~/.local/share/FireVPN, а не внутри приложения (туда macOS писать не даёт)
echo "When this file exists, app will not store configs under this folder" > "$App/Contents/MacOS/NotStoreConfigHere.txt"
chmod +x "$App/Contents/MacOS/v2rayN"

cat >"$App/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>ru</string>
  <key>CFBundleLocalizations</key>
  <array>
    <string>ru</string>
    <string>en</string>
  </array>
  <key>CFBundleDisplayName</key>
  <string>FireVPN</string>
  <key>CFBundleName</key>
  <string>FireVPN</string>
  <key>CFBundleExecutable</key>
  <string>v2rayN</string>
  <key>CFBundleIconFile</key>
  <string>AppIcon</string>
  <key>CFBundleIconName</key>
  <string>AppIcon</string>
  <key>CFBundleIdentifier</key>
  <string>online.firevpn.desktop</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>${Version}</string>
  <key>CFBundleVersion</key>
  <string>${Version}</string>
  <key>CFBundleURLTypes</key>
  <array>
    <dict>
      <key>CFBundleURLName</key>
      <string>online.firevpn.link</string>
      <key>CFBundleURLSchemes</key>
      <array>
        <string>firevpn</string>
      </array>
    </dict>
  </array>
  <key>CSResourcesFileMapped</key>
  <true/>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>LSMinimumSystemVersion</key>
  <string>13.6</string>
</dict>
</plist>
EOF

# подпись «без разработчика» (ad-hoc): без неё на Маке с Apple M программа вообще не запустится.
# Предупреждение «не удалось проверить разработчика» она не убирает — для этого нужен аккаунт Apple.
codesign --force --deep --sign - "$App"

create-dmg \
    --volname "FireVPN" \
    --window-size 640 400 \
    --icon-size 110 \
    --icon "FireVPN.app" 170 180 \
    --hide-extension "FireVPN.app" \
    --app-drop-link 470 180 \
    "FireVPN-${Version}-macos-${Arch}.dmg" \
    "FireVPN-Package-${Arch}/"
