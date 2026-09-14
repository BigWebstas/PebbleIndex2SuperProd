#!/usr/bin/env bash
# Builds Linux AppImage / .deb / .rpm packages, plus a PKGBUILD for Arch.
#
# makepkg itself is NOT run here — it needs an Arch host (pacman, fakeroot's Arch-specific
# behaviour) that a plain Ubuntu CI runner isn't, and spinning up an Arch container for one
# recipe file isn't worth it. Instead this generates the PKGBUILD as a release asset, the same
# thing most small projects hand to the AUR — an Arch user runs `makepkg -si` against it. The
# PKGBUILD's `options=('!strip')` matters: makepkg's default install step runs `strip` on every
# binary it packages, which corrupts a self-contained single-file .NET bundle (confirmed by
# hand — a stripped copy fails to start with "Failure processing application bundle").
#
# Requires: .NET 8 SDK, appimagetool (on PATH or at $APPIMAGETOOL), fpm (with rpmbuild installed
# for the .rpm target — `gem install fpm`, `apt-get install rpm` on Debian/Ubuntu).
#
#   scripts/package-linux-formats.sh [version]
set -euo pipefail

version="${1:-1.0.0}"
rid="linux-x64"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/src/Index2SP/Index2SP.csproj"
dist="$root/dist"
work="$root/artifacts/linux-formats"
publish_dir="$root/artifacts/publish/linux-self-contained"
icon="$root/packaging/linux/index2sp.png"
desktop="$root/packaging/linux/index2sp.desktop"
appimagetool="${APPIMAGETOOL:-appimagetool}"
mkdir -p "$dist" "$work"

# Reuse the self-contained publish from package-linux.sh if it already ran; otherwise publish so
# this script also works standalone.
if [ ! -x "$publish_dir/Index2SP" ]; then
  echo "==> publish linux self-contained (v$version)"
  dotnet publish "$project" -c Release -r "$rid" --self-contained true \
    -o "$publish_dir" "/p:Version=$version"
fi
chmod +x "$publish_dir/Index2SP"

# ---- AppImage --------------------------------------------------------------
build_appimage() {
  echo "==> AppImage"
  local appdir="$work/AppDir"
  rm -rf "$appdir"
  mkdir -p "$appdir/usr/bin"
  cp "$publish_dir/Index2SP" "$appdir/usr/bin/index2sp"
  cp "$icon" "$appdir/index2sp.png"
  sed "s|^Exec=.*|Exec=index2sp|" "$desktop" > "$appdir/index2sp.desktop"
  cat > "$appdir/AppRun" <<'RUNEOF'
#!/usr/bin/env bash
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec "$here/usr/bin/index2sp" "$@"
RUNEOF
  chmod +x "$appdir/AppRun"

  local out="$dist/Index2SP-x86_64-${version}.AppImage"
  rm -f "$out"
  # --appimage-extract-and-run: CI runners generally don't have FUSE, which a normal AppImage
  # (appimagetool is itself one) needs to mount itself without this flag.
  ARCH=x86_64 "$appimagetool" --appimage-extract-and-run "$appdir" "$out"
  echo "    -> $out"
}

# ---- .deb / .rpm via fpm -----------------------------------------------------
build_fpm_package() {
  local format="$1"
  echo "==> $format"
  local pkgroot="$work/pkgroot-$format"
  rm -rf "$pkgroot"
  mkdir -p "$pkgroot/usr/bin" "$pkgroot/usr/share/applications" \
           "$pkgroot/usr/share/icons/hicolor/256x256/apps"
  install -m 0755 "$publish_dir/Index2SP" "$pkgroot/usr/bin/index2sp"
  sed "s|^Exec=.*|Exec=index2sp|" "$desktop" > "$pkgroot/usr/share/applications/index2sp.desktop"
  install -m 0644 "$icon" "$pkgroot/usr/share/icons/hicolor/256x256/apps/index2sp.png"

  fpm -s dir -t "$format" -n index2sp -v "$version" \
    --license MIT \
    --description "Pebble Index 01 webhook -> Super Productivity task bridge" \
    --url "https://github.com/BigWebstas/PebbleIndex2SuperProd" \
    --maintainer "BigWebstas" \
    -C "$pkgroot" -p "$dist/" \
    usr
}

# ---- PKGBUILD (Arch) — generated, not built here (see the header comment) --
build_pkgbuild() {
  echo "==> PKGBUILD"
  local tarball="$dist/Index2SP-linux-x64-${version}.tar.gz"
  if [ ! -f "$tarball" ]; then
    echo "    (skipped — run scripts/package-linux.sh first so $tarball exists to hash)"
    return
  fi
  local sha
  sha="$(sha256sum "$tarball" | cut -d' ' -f1)"
  cat > "$dist/PKGBUILD" <<EOF
# Maintainer: BigWebstas
pkgname=index2sp
pkgver=${version}
pkgrel=1
pkgdesc="Pebble Index 01 webhook -> Super Productivity task bridge"
arch=('x86_64')
url="https://github.com/BigWebstas/PebbleIndex2SuperProd"
license=('MIT')
depends=()
options=('!strip')
source=("https://github.com/BigWebstas/PebbleIndex2SuperProd/releases/download/v${version}/Index2SP-linux-x64-${version}.tar.gz")
sha256sums=('${sha}')

package() {
  install -Dm755 "\$srcdir/Index2SP" "\$pkgdir/usr/bin/index2sp"
  install -Dm644 "\$srcdir/index2sp.desktop" "\$pkgdir/usr/share/applications/index2sp.desktop"
  install -Dm644 "\$srcdir/index2sp.png" "\$pkgdir/usr/share/icons/hicolor/256x256/apps/index2sp.png"
}
EOF
  echo "    -> $dist/PKGBUILD"
}

build_appimage
build_fpm_package deb
build_fpm_package rpm
build_pkgbuild

echo
ls -la "$dist"
