#!/usr/bin/env bash
#
# Build XDM. Tự chọn SDK sẵn có (dotnet trên PATH, ~/.dotnet, hoặc container podman/docker).
#
#   ./build.sh                     # build UI Linux (GTK), Release
#   ./build.sh -c Debug            # đổi cấu hình build
#   ./build.sh --wpf               # build thêm UI Windows (WPF)
#   ./build.sh --publish           # publish bản GTK tự chứa + trim (để chạy thật trên Linux)
#   ./build.sh --wpf --publish     # làm cả hai
#
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CONFIG="Release"
BUILD_WPF=0
PUBLISH=0

while [ $# -gt 0 ]; do
    case "$1" in
        -c|--configuration) CONFIG="${2:?thiếu giá trị cấu hình}"; shift 2 ;;
        --wpf) BUILD_WPF=1; shift ;;
        --publish) PUBLISH=1; shift ;;
        -h|--help) sed -n '3,9p' "${BASH_SOURCE[0]}"; exit 0 ;;
        *) echo "Tuỳ chọn không hợp lệ: $1" >&2; exit 2 ;;
    esac
done

GTK_PROJ="app/XDM/XDM.Gtk.UI/XDM.Gtk.UI.csproj"
WPF_PROJ="app/XDM/XDM.Wpf.UI/XDM.Wpf.UI.csproj"
SDK_IMAGE="mcr.microsoft.com/dotnet/sdk:6.0"

# --- chọn cách chạy dotnet --------------------------------------------------
if command -v dotnet >/dev/null 2>&1; then
    DOTNET=(dotnet)
elif [ -x "$HOME/.dotnet/dotnet" ]; then
    DOTNET=("$HOME/.dotnet/dotnet")
elif command -v podman >/dev/null 2>&1 || command -v docker >/dev/null 2>&1; then
    RUNTIME="$(command -v podman >/dev/null 2>&1 && command -v podman || command -v docker)"
    "$RUNTIME" image inspect "$SDK_IMAGE" >/dev/null 2>&1 || "$RUNTIME" pull "$SDK_IMAGE"
    CACHE_DIR="$HOME/.cache/xdm-nuget"
    mkdir -p "$CACHE_DIR"
    DOTNET=("$RUNTIME" run --rm -v "$ROOT_DIR:/src:z" -v "$CACHE_DIR:/root/.nuget/packages:z"
        -w /src "$SDK_IMAGE" dotnet)
    echo "== dotnet: container $SDK_IMAGE ($(basename "$RUNTIME"))"
else
    echo "Không có dotnet trên PATH, không có ~/.dotnet/dotnet, cũng không có podman/docker." >&2
    exit 1
fi

run() { echo "+ ${DOTNET[*]} $*"; "${DOTNET[@]}" "$@"; }

# --- build ------------------------------------------------------------------
run build "$GTK_PROJ" -c "$CONFIG"

if [ "$BUILD_WPF" -eq 1 ]; then
    # WPF cần targeting pack của Windows -> phải bật EnableWindowsTargeting khi build trên Linux
    run build "$WPF_PROJ" -c "$CONFIG" -p:EnableWindowsTargeting=true
fi

if [ "$PUBLISH" -eq 1 ]; then
    run publish "$GTK_PROJ" -c "$CONFIG" -r linux-x64 --self-contained
fi

echo
echo "Xong:"
echo "  GTK          : $ROOT_DIR/app/XDM/XDM.Gtk.UI/bin/$CONFIG/net6.0/xdm-app.dll"
if [ "$BUILD_WPF" -eq 1 ]; then
    echo "  WPF          : $ROOT_DIR/app/XDM/XDM.Wpf.UI/bin/$CONFIG/net4.7.2/xdm-app.exe (build trên Linux chỉ để kiểm tra biên dịch)"
fi
if [ "$PUBLISH" -eq 1 ]; then
    echo "  GTK (publish): $ROOT_DIR/app/XDM/XDM.Gtk.UI/bin/$CONFIG/net6.0/linux-x64/publish/xdm-app"
fi
