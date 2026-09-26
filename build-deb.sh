#!/usr/bin/env bash
# Publish ứng dụng GTK và tạo gói Debian tại dist/.
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$ROOT_DIR"

command -v dpkg-deb >/dev/null || { echo "Thiếu dpkg-deb" >&2; exit 1; }
[ "$(dpkg --print-architecture)" = amd64 ] || { echo "Script này chỉ đóng gói linux-x64/amd64" >&2; exit 1; }

VERSION="$(sed -n 's/.*APP_VERSION *= *"\([^"]*\)".*/\1/p' app/XDM/XDM.Core/AppInfo.cs)"
[ -n "$VERSION" ] || { echo "Không đọc được APP_VERSION" >&2; exit 1; }

./build.sh --publish
PUBLISH_DIR="app/XDM/XDM.Gtk.UI/bin/Release/net6.0/linux-x64/publish"
[ -x "$PUBLISH_DIR/xdm-app" ] || { echo "Thiếu bản publish: $PUBLISH_DIR/xdm-app" >&2; exit 1; }
[ -f "$PUBLISH_DIR/xdm-logo.svg" ] || { echo "Thiếu biểu tượng trong bản publish" >&2; exit 1; }

mkdir -p dist
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/DEBIAN" "$STAGE/opt/xdman" "$STAGE/usr/bin" "$STAGE/usr/share/applications"
cp -a "$PUBLISH_DIR/." "$STAGE/opt/xdman/"

cat > "$STAGE/DEBIAN/control" <<EOF
Package: xdman
Version: $VERSION
Architecture: amd64
Depends: libgtk-3-0 (>= 3.22.11)
Recommends: ffmpeg
Maintainer: Subhra Das Gupta
Description: Open source download accelerator and video downloader
 Xtreme Download Manager for Linux (GTK).
EOF

cat > "$STAGE/usr/share/applications/xdm-app.desktop" <<'EOF'
[Desktop Entry]
Version=1.0
Type=Application
Name=Xtreme Download Manager
Comment=Download manager and video downloader
Exec=env GTK_USE_PORTAL=1 /opt/xdman/xdm-app %U
Icon=/opt/xdman/xdm-logo.svg
Terminal=false
Categories=Network;
MimeType=application/xdm-app;x-scheme-handler/xdm-app;
StartupNotify=true
EOF
cp "$STAGE/usr/share/applications/xdm-app.desktop" "$STAGE/opt/xdman/xdm-app.desktop"

cat > "$STAGE/usr/bin/xdman" <<'EOF'
#!/bin/sh
export GTK_USE_PORTAL=1
exec /opt/xdman/xdm-app "$@"
EOF
chmod 755 "$STAGE/usr/bin/xdman"
printf '%s\n' 'xdman_gtk|.deb' > "$STAGE/opt/xdman/source_pkg"
printf '%s\n' 'xdman_gtk|.deb' > "$STAGE/opt/xdman/source_pkg.txt"

PACKAGE="dist/xdman_gtk_${VERSION}_amd64.deb"
dpkg-deb --build --root-owner-group -Zxz "$STAGE" "$PACKAGE"
echo "Gói đã tạo: $ROOT_DIR/$PACKAGE"
