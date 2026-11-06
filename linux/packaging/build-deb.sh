#!/usr/bin/env bash
# Builds ssh-tailscale-manager_<version>_all.deb  (usage: build-deb.sh <version> [outdir])
set -euo pipefail
VERSION="${1:?version required}"
OUT="${2:-.}"
HERE="$(cd "$(dirname "$0")" && pwd)"
LINUX="$HERE/.."
ROOT="$(mktemp -d)"
PKG="$ROOT/ssh-tailscale-manager_${VERSION}_all"

install -d "$PKG/DEBIAN" "$PKG/usr/lib/ssh-tailscale-manager" "$PKG/usr/bin" \
  "$PKG/usr/share/applications" "$PKG/usr/share/icons/hicolor/256x256/apps"
cp -r "$LINUX/sshtsm" "$PKG/usr/lib/ssh-tailscale-manager/"
find "$PKG" -name '__pycache__' -prune -exec rm -rf {} +
install -m 755 "$HERE/ssh-tailscale-manager" "$PKG/usr/bin/ssh-tailscale-manager"
install -m 644 "$HERE/ssh-tailscale-manager.desktop" "$PKG/usr/share/applications/"
install -m 644 "$LINUX/sshtsm/icon.png" "$PKG/usr/share/icons/hicolor/256x256/apps/ssh-tailscale-manager.png"
sed "s/@VERSION@/$VERSION/" "$HERE/control" > "$PKG/DEBIAN/control"
find "$PKG" -type d -exec chmod 755 {} +
find "$PKG/usr/lib" -type f -exec chmod 644 {} +

mkdir -p "$OUT"
dpkg-deb --build --root-owner-group "$PKG" "$OUT/ssh-tailscale-manager_${VERSION}_all.deb"
rm -rf "$ROOT"
