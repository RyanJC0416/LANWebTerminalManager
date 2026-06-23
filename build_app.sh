#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")" && pwd)"
BUILD_CONFIG="${BUILD_CONFIG:-release}"
APP_NAME="局域网网页终端管理器"
BUNDLE_ID="local.ryan.lan-web-terminal-manager"
APP_DIR="$ROOT_DIR/build/${APP_NAME}.app"
RELEASE_DIR="$ROOT_DIR/release"
RELEASE_APP_DIR="$RELEASE_DIR/${APP_NAME}.app"
RELEASE_ZIP="$RELEASE_DIR/${APP_NAME}.app.zip"
RELEASE_STABLE_ZIP="$RELEASE_DIR/app.zip"
CONTENTS="$APP_DIR/Contents"
MACOS="$CONTENTS/MacOS"
RESOURCES="$CONTENTS/Resources"

cd "$ROOT_DIR"
swift build -c "$BUILD_CONFIG"

rm -rf "$APP_DIR"
mkdir -p "$MACOS" "$RESOURCES"
cp ".build/$BUILD_CONFIG/LANWebTerminalManager" "$MACOS/$APP_NAME"

cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>zh_CN</string>
  <key>CFBundleDisplayName</key>
  <string>${APP_NAME}</string>
  <key>CFBundleExecutable</key>
  <string>${APP_NAME}</string>
  <key>CFBundleIdentifier</key>
  <string>${BUNDLE_ID}</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>${APP_NAME}</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>1.0.1</string>
  <key>CFBundleVersion</key>
  <string>1</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.utilities</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
</dict>
</plist>
PLIST

cat > "$RESOURCES/README.txt" <<TXT
${APP_NAME}

原生 SwiftUI macOS app，用于管理多个局域网网页目录：
- 启动/停止 Python 静态网页服务
- 展示本机与局域网访问地址
- 打开网页或目录
- 在目录内执行维护命令
- 查看服务日志
TXT

chmod +x "$MACOS/$APP_NAME"

rm -rf "$RELEASE_APP_DIR" "$RELEASE_ZIP" "$RELEASE_STABLE_ZIP"
mkdir -p "$RELEASE_DIR"
cp -R "$APP_DIR" "$RELEASE_APP_DIR"
ditto -c -k --keepParent "$RELEASE_APP_DIR" "$RELEASE_ZIP"
cp "$RELEASE_ZIP" "$RELEASE_STABLE_ZIP"

echo "已构建: $APP_DIR"
echo "已发布包: $RELEASE_ZIP"
echo "已发布包: $RELEASE_STABLE_ZIP"
