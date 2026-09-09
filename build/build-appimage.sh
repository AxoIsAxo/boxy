#!/usr/bin/env bash
# Build a standalone amd64 AppImage for Boxy from the .NET + GTK4 source.
#
# Distro-agnostic: works on RHEL/Rocky (/usr/lib64) and Ubuntu/Debian
# (/usr/lib/x86_64-linux-gnu). Library directories are resolved via pkg-config
# with a SONAME-aware fallback, so the script does not hardcode host paths.
#
# Requires (installed by CI or locally):
#   - dotnet SDK 10
#   - libgtk-4-dev, libadwaita-1-dev, libgdk-pixbuf2.0-dev, librsvg2-dev,
#     gsettings-desktop-schemas, fonts-dejavu-core, binutils, librsvg2-bin,
#     appimagetool (on PATH)
#
# Usage:  bash build/build-appimage.sh
# Output: build/AppDir -> build/Boxy-x86_64.AppImage

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ASSETS="$SCRIPT_DIR/assets"
APP="$SCRIPT_DIR/AppDir"
RID=linux-x64
OUT="Boxy-x86_64.AppImage"

log() { echo "==> $*"; }

# ---- locate native library dir (multiarch-aware) ----
find_libdir() {
  local d
  # pkg-config knows the correct libdir for the GTK stack on Debian/Ubuntu.
  d="$(pkg-config --variable=libdir gtk4 2>/dev/null || true)"
  [ -n "$d" ] && printf '%s\n' "$d" && return 0
  for d in /usr/lib64 /usr/lib/x86_64-linux-gnu /usr/lib; do
    [ -f "$d/libgtk-4.so.1" ] && printf '%s\n' "$d" && return 0
  done
  printf '%s\n' "."
}

LIBDIR="$(find_libdir)"
log "Library dir: $LIBDIR"

# Resolve a library path by SONAME, searching LIBDIR first.
lib() {
  local soname="$1" p
  [ -e "$LIBDIR/$soname" ] && { printf '%s' "$LIBDIR/$soname"; return; }
  # fall back to a recursive search of the system (handles loader modules elsewhere)
  p="$(find /usr/lib /usr/lib64 /lib /lib64 -name "$soname" -print -quit 2>/dev/null || true)"
  printf '%s' "${p:-$LIBDIR/$soname}"
}

# ---- 1. Self-contained .NET publish (bundles the CLR; host needs no .NET) ----
log "Self-contained .NET publish ($RID)"
if [ -f "$SCRIPT_DIR/../Boxy.csproj" ]; then
  SRC="$SCRIPT_DIR/.."
else
  SRC="$SCRIPT_DIR"
fi
dotnet publish "$SRC/Boxy.csproj" -c Release -r "$RID" --self-contained true -o "$SCRIPT_DIR/publish"

# ---- 2. Reset the AppDir ----
chmod -R u+w "$APP" 2>/dev/null || true
rm -rf "$APP"
mkdir -p "$APP/usr/lib/boxy" "$APP/usr/lib/gio/modules" \
         "$APP/usr/lib/gdk-pixbuf-2.0/2.10.0/loaders" \
         "$APP/usr/share/applications" "$APP/usr/share/glib-2.0/schemas" \
         "$APP/usr/share/icons" "$APP/usr/share/fonts" "$APP/etc/fonts/conf.d"

# ---- 3. Stage the published app ----
cp -a "$SCRIPT_DIR/publish/." "$APP/usr/lib/boxy/"

# ---- 4. Bundle GTK4/libadwaita + all transitive native deps ----
# Seed = the libraries GirCore dlopens at runtime. Resolved per-distro.
SEED=(
  "$(lib libgtk-4.so.1)" "$(lib libadwaita-1.so.0)" "$(lib libgdk-4.so.1)"
  "$(lib libgdk_pixbuf-2.0.so.0)" "$(lib libcairo.so.2)" "$(lib libcairo-gobject.so.2)"
  "$(lib libpango-1.0.so.0)" "$(lib libpangocairo-1.0.so.0)" "$(lib libpangoft2-1.0.so.0)"
  "$(lib libharfbuzz.so.0)" "$(lib libglib-2.0.so.0)" "$(lib libgobject-2.0.so.0)"
  "$(lib libgio-2.0.so.0)" "$(lib libgmodule-2.0.so.0)" "$(lib librsvg-2.so.2)"
  "$(lib libgraphene-1.0.so.0)" "$(lib libfribidi.so.0)" "$(lib libfontconfig.so.1)"
  "$(lib libpixman-1.so.0)" "$(lib libwayland-client.so.0)" "$(lib libwayland-egl.so.1)"
  "$(lib libxkbcommon.so.0)" "$(lib libjson-glib-1.0.so.0)" "$(lib libepoxy.so.0)"
)

log "Bundling native libraries (${#SEED[@]} seeds)..."
tmp="$(mktemp -d)"
chmod +x "$ASSETS/lddtree.sh"
# Collect the whole dependency closure (including the seeds) to a file (no pipe -> no SIGPIPE).
"$ASSETS/lddtree.sh" "${SEED[@]}" > "$tmp/raw" 2>/dev/null || true
sort -u "$tmp/raw" > "$tmp/libs"
while read -r lfile; do
  [ -f "$lfile" ] || continue
  real="$(readlink -f "$lfile")"; base="$(basename "$real")"
  [ -f "$APP/usr/lib/$base" ] || cp -a "$real" "$APP/usr/lib/$base"
done < "$tmp/libs"
rm -rf "$tmp"

# Plant SONAME symlinks (soname -> real file) from each ELF's DT_SONAME.
for f in "$APP/usr/lib/"*.so*; do
  [ -f "$f" ] || continue
  buf="$(readelf -d "$f" 2>/dev/null || true)"
  soname="$(printf '%s\n' "$buf" | sed -n 's/.*SONAME *\[\([^]]*\)\].*/\1/p')"
  if [ -n "$soname" ] && [ "$soname" != "$(basename "$f")" ]; then ln -sf "$(basename "$f")" "$APP/usr/lib/$soname"; fi
done

# ---- 5. gdk-pixbuf loader modules + query tool ----
PIXBUF_LOADERS="$(dirname "$(lib libgdk_pixbuf-2.0.so.0)")/gdk-pixbuf-2.0/2.10.0/loaders"
[ -d "$PIXBUF_LOADERS" ] || PIXBUF_LOADERS="$(find /usr/lib /usr/lib64 /lib /lib64 -path '*gdk-pixbuf-2.0/2.10.0/loaders' -type d -print -quit 2>/dev/null || true)"
for l in libpixbufloader-svg.so libpixbufloader-gif.so libpixbufloader-tiff.so; do
  if [ -f "$PIXBUF_LOADERS/$l" ]; then cp -a "$PIXBUF_LOADERS/$l" "$APP/usr/lib/gdk-pixbuf-2.0/2.10.0/loaders/"; fi
done
# bundle a query-loaders tool for the runtime cache generation
QTOOL="$(command -v gdk-pixbuf-query-loaders 2>/dev/null || true)"
[ -z "$QTOOL" ] && QTOOL="$(command -v gdk-pixbuf-query-loaders-64 2>/dev/null || true)"
if [ -z "$QTOOL" ]; then
  QTOOL="$(find /usr /lib -name 'gdk-pixbuf-query-loaders*' -type f -print -quit 2>/dev/null || true)"
fi
if [ -n "$QTOOL" ]; then
  cp -a "$QTOOL" "$APP/usr/lib/gdk-pixbuf-query-loaders"
  chmod +x "$APP/usr/lib/gdk-pixbuf-query-loaders"
else
  log "WARNING: no gdk-pixbuf query-loaders tool found; runtime won't auto-gen loader cache"
fi

# ---- 6. GIO modules (network for AUR RPC) ----
GIO_MODS="$(find /usr/lib /usr/lib64 /lib /lib64 -path '*gio/modules' -type d -print -quit 2>/dev/null || true)"
for m in libgiognutls.so libgiognomeproxy.so libgiolibproxy.so libgioremote-volume-monitor.so; do
  if [ -n "$GIO_MODS" ] && [ -f "$GIO_MODS/$m" ]; then cp -a "$GIO_MODS/$m" "$APP/usr/lib/gio/modules/"; fi
done

# ---- 7. GSettings schemas (libadwaita/GTK styling) ----
if [ -d /usr/share/glib-2.0/schemas ]; then cp -a /usr/share/glib-2.0/schemas/. "$APP/usr/share/glib-2.0/schemas/"; fi

# ---- 8. Icon themes + Boxy app icon ----
if [ -d /usr/share/icons/Adwaita ]; then cp -a /usr/share/icons/Adwaita "$APP/usr/share/icons/"; fi
if [ -d /usr/share/icons/hicolor ]; then cp -a /usr/share/icons/hicolor "$APP/usr/share/icons/"; fi
rm -rf "$APP/usr/share/icons/Adwaita/cursors" 2>/dev/null || true
# strip stray system app icons, keep only boxy
for sz in 256x256 512x512 64x64 48x48 128x128 32x32; do
  d="$APP/usr/share/icons/hicolor/$sz/apps"; [ -d "$d" ] && { find "$d" -type f ! -name 'boxy.png' -delete 2>/dev/null; find "$d" -type l -delete 2>/dev/null; }
done
mkdir -p "$APP/usr/share/icons/hicolor/256x256/apps" "$APP/usr/share/icons/hicolor/512x512/apps"
rsvg-convert -w 256 -h 256 "$ASSETS/boxy-icon.svg" -o "$APP/usr/share/icons/hicolor/256x256/apps/boxy.png"
rsvg-convert -w 512 -h 512 "$ASSETS/boxy-icon.svg" -o "$APP/usr/share/icons/hicolor/512x512/apps/boxy.png"
rsvg-convert "$ASSETS/boxy-icon.svg" -o "$APP/.DirIcon"
cp "$APP/usr/share/icons/hicolor/512x512/apps/boxy.png" "$APP/boxy.png"

# ---- 9. Fonts (DejaVu) ----
for f in dejavu-sans-fonts dejavu-sans-mono-fonts dejavu-serif-fonts; do
  if [ -d "/usr/share/fonts/$f" ]; then cp -a "/usr/share/fonts/$f" "$APP/usr/share/fonts/"; fi
done
# Ubuntu puts DejaVu under /usr/share/fonts/truetype/dejavu
if [ ! -d "$APP/usr/share/fonts/dejavu-sans-fonts" ]; then
  d="$(find /usr/share/fonts -maxdepth 3 -name DejaVuSans.ttf -print -quit 2>/dev/null | xargs dirname 2>/dev/null || true)"
  if [ -n "$d" ] && [ -d "$d" ]; then cp -a "$d/." "$APP/usr/share/fonts/dejavu/"; fi
fi

# ---- 10. desktop + AppRun (AppRun generates a runtime fonts.conf pointing at the
#           bundled fonts, so no static fonts.conf is needed here) ----
cp "$ASSETS/boxy.desktop" "$APP/boxy.desktop"
cp "$ASSETS/boxy.desktop" "$APP/usr/share/applications/boxy.desktop"
cp "$ASSETS/AppRun" "$APP/AppRun" && chmod +x "$APP/AppRun"

# ---- 11. Package ----
log "Packaging AppImage..."
cd "$SCRIPT_DIR"
ARCH=x86_64 appimagetool AppDir
ls -la "$SCRIPT_DIR/$OUT"
log "Done: $SCRIPT_DIR/$OUT"
