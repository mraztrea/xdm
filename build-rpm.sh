#!/usr/bin/env bash
#
# Đóng gói XDM (GTK, Linux) thành .rpm.
#
# Theo đúng layout của app/packaging/make-rpm-pkg: toàn bộ bản publish nằm trong
# /opt/xdman, lệnh khởi chạy /usr/bin/xdman, desktop entry + source_pkg cho updater.
# Cần rpmbuild (Fedora: sudo dnf install rpm-build).
#
#   ./build-rpm.sh                      # publish (./build.sh --publish) rồi đóng gói vào dist/
#   ./build-rpm.sh --no-build           # dùng lại bản publish đã có
#   ./build-rpm.sh --version 8.0.26 --release 2
#   ./build-rpm.sh --arch aarch64 --rid linux-arm64
#
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG_NAME="xdman_gtk"          # tên gói rpm, cũng là prefix asset trong source_pkg
INSTALL_DIR="/opt/xdman"
LAUNCHER="xdman"
LICENSE_TAG="GPL-2.0-only"    # theo LICENSE ở gốc repo (GPL v2)
DESKTOP_NAME="xdm-app.desktop"
CONFIG="Release"
OUT_DIR="$ROOT_DIR/dist"
DO_BUILD=1

# phiên bản lấy từ nguồn duy nhất: XDM.Core/AppInfo.cs
APP_VERSION="$(sed -n 's/.*APP_VERSION *= *"\([^"]*\)".*/\1/p' "$ROOT_DIR/app/XDM/XDM.Core/AppInfo.cs")"
RELEASE="1"
ARCH="$(uname -m)"
case "$ARCH" in
    x86_64) RID="linux-x64" ;;
    aarch64) RID="linux-arm64" ;;
    *) RID="linux-$ARCH" ;;
esac

while [ $# -gt 0 ]; do
    case "$1" in
        -c|--configuration) CONFIG="${2:?thiếu giá trị}"; shift 2 ;;
        --arch) ARCH="${2:?thiếu giá trị}"; shift 2 ;;
        --rid) RID="${2:?thiếu giá trị}"; shift 2 ;;
        --version) APP_VERSION="${2:?thiếu giá trị}"; shift 2 ;;
        --release) RELEASE="${2:?thiếu giá trị}"; shift 2 ;;
        -o|--output) OUT_DIR="${2:?thiếu giá trị}"; shift 2 ;;
        --no-build) DO_BUILD=0; shift ;;
        -h|--help) sed -n '3,12p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "Tuỳ chọn không hợp lệ: $1" >&2; exit 2 ;;
    esac
done

[ -n "$APP_VERSION" ] || { echo "Không đọc được APP_VERSION từ AppInfo.cs" >&2; exit 1; }
command -v rpmbuild >/dev/null 2>&1 || {
    echo "Thiếu rpmbuild. Fedora: sudo dnf install rpm-build" >&2; exit 1; }

PUBLISH_DIR="$ROOT_DIR/app/XDM/XDM.Gtk.UI/bin/$CONFIG/net6.0/$RID/publish"

if [ "$DO_BUILD" -eq 1 ]; then
    "$ROOT_DIR/build.sh" -c "$CONFIG" --publish
fi
if [ ! -x "$PUBLISH_DIR/xdm-app" ]; then
    echo "Không thấy bản publish: $PUBLISH_DIR/xdm-app" >&2
    echo "Chạy lại không kèm --no-build, hoặc kiểm tra --configuration/--rid." >&2
    exit 1
fi

# --- dựng cây nguồn (nội dung tarball giống hệt khi cài đặt) --------------------
SOURCE_NAME="$PKG_NAME-$APP_VERSION"
TOP_DIR="$OUT_DIR/rpmbuild"
SRC_DIR="$TOP_DIR/$SOURCE_NAME"

rm -rf "$TOP_DIR"
mkdir -p "$TOP_DIR"/{BUILD,RPMS,SOURCES,SPECS,SRPMS} \
    "$SRC_DIR$INSTALL_DIR" "$SRC_DIR/usr/bin" "$SRC_DIR/usr/share/applications"

cp -a "$PUBLISH_DIR/." "$SRC_DIR$INSTALL_DIR/"

# updater đọc source_pkg (không phần mở rộng) để biết cách tải bản cập nhật;
# bản publish có sẵn source_pkg.txt nội dung "deb" nên ghi lại cho khớp gói rpm này
printf '%s\n' "$PKG_NAME|.rpm" > "$SRC_DIR$INSTALL_DIR/source_pkg"
printf '%s\n' "$PKG_NAME|.rpm" > "$SRC_DIR$INSTALL_DIR/source_pkg.txt"

cat > "$SRC_DIR$INSTALL_DIR/$DESKTOP_NAME" <<DESKTOP
[Desktop Entry]
Version=1.0
Encoding=UTF-8
Exec=env GTK_USE_PORTAL=1 $INSTALL_DIR/xdm-app %U
Type=Application
Terminal=false
Name=Xtreme Download Manager
Comment=Xtreme Download Manager
Categories=Network;
Icon=$INSTALL_DIR/xdm-logo.svg
MimeType=application/xdm-app;x-scheme-handler/xdm-app;
StartupNotify=true
DESKTOP
install -m 0644 "$SRC_DIR$INSTALL_DIR/$DESKTOP_NAME" "$SRC_DIR/usr/share/applications/$DESKTOP_NAME"

cat > "$SRC_DIR/usr/bin/$LAUNCHER" <<LAUNCHER
#!/bin/bash
export GTK_USE_PORTAL=1
exec $INSTALL_DIR/xdm-app "\$@"
LAUNCHER
chmod 0755 "$SRC_DIR/usr/bin/$LAUNCHER" "$SRC_DIR$INSTALL_DIR/xdm-app"

tar -C "$TOP_DIR" -czf "$TOP_DIR/SOURCES/$SOURCE_NAME.tar.gz" "$SOURCE_NAME"

# --- spec ---------------------------------------------------------------------
cat > "$TOP_DIR/SPECS/$PKG_NAME.spec" <<'SPEC'
# bản publish self-contained: không tạo debuginfo, không đòi build-id
%global debug_package %{nil}
%global _build_id_links none
%undefine _missing_build_ids_terminate_build

Name:        @NAME@
Version:     @VERSION@
Release:     @RELEASE@%{?dist}
Summary:     Xtreme Download Manager
License:     @LICENSE@
Source0:     @SOURCE@
BuildArch:   @ARCH@
Group:       System Environment/Base
AutoReqProv: no
Requires:    gtk3 >= 3.22
Requires:    ffmpeg-free
# icon SVG/PNG của app do glycin-loaders nạp, gói này được kéo theo sẵn:
# gtk3 -> gdk-pixbuf2 -> glycin-libs -> glycin-loaders (-> bubblewrap)

%description
Open source download accelerator and video downloader.
Bản Linux (GTK) của XDM, publish self-contained .NET 6, cài trong @INSTALL_DIR@.

%prep
%setup -q

%install
cp -rfa opt usr %{buildroot}

%post
update-desktop-database %{_datadir}/applications &>/dev/null || :

%postun
update-desktop-database %{_datadir}/applications &>/dev/null || :

%files
/usr/bin/*
/usr/share/applications/*
@INSTALL_DIR@/*
SPEC

sed -i -e "s|@NAME@|$PKG_NAME|g" -e "s|@VERSION@|$APP_VERSION|g" \
    -e "s|@RELEASE@|$RELEASE|g" -e "s|@ARCH@|$ARCH|g" \
    -e "s|@INSTALL_DIR@|$INSTALL_DIR|g" \
    -e "s|@LICENSE@|$LICENSE_TAG|g" \
    -e "s|@SOURCE@|$SOURCE_NAME.tar.gz|g" \
    "$TOP_DIR/SPECS/$PKG_NAME.spec"

echo "+ rpmbuild -bb --define '_topdir $TOP_DIR' $TOP_DIR/SPECS/$PKG_NAME.spec"
rpmbuild -bb --define "_topdir $TOP_DIR" "$TOP_DIR/SPECS/$PKG_NAME.spec"

echo
echo "Xong:"
find "$TOP_DIR/RPMS" -name '*.rpm' -printf '  %p (%s bytes)\n'
echo
echo "Xem nội dung : rpm -qpl $OUT_DIR/rpmbuild/RPMS/$ARCH/*.rpm"
echo "Cài đặt      : sudo dnf install $OUT_DIR/rpmbuild/RPMS/$ARCH/*.rpm"
