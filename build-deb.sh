#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
VERSION=${VERSION:-0.1.39}
ARCH=${ARCH:-arm64}
OUTPUT="$ROOT/dist"
STAGE="$ROOT/.package-root"
PUBLISH="$ROOT/.publish"

case "$ARCH" in
    arm64|amd64|armhf) ;;
    *)
        echo "Unsupported package architecture: $ARCH" >&2
        exit 2
        ;;
esac

case "$VERSION" in
    *[!0-9A-Za-z.+:~-]*|'')
        echo "Invalid Debian package version: $VERSION" >&2
        exit 2
        ;;
esac

case "$ARCH" in
    armhf) RID=linux-arm ;;
    *) RID="linux-$ARCH" ;;
esac

rm -rf "$STAGE" "$PUBLISH"
mkdir -p "$OUTPUT" "$STAGE/DEBIAN" "$STAGE/etc/dpm/sources.list.d" "$STAGE/usr/bin" "$STAGE/usr/share/doc/dpm" "$STAGE/usr/share/doc/dpm/examples"

"${DOTNET:-dotnet}" publish "$ROOT/dpm.csproj" \
    --configuration Release \
    --runtime "$RID" \
    --self-contained true \
    --maxcpucount:1 \
    -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true \
    --output "$PUBLISH"

install -m 0755 "$PUBLISH/dpm" "$STAGE/usr/bin/dpm"
install -m 0644 "$ROOT/README.md" "$STAGE/usr/share/doc/dpm/README.md"
install -m 0644 "$ROOT/tuna.list" "$STAGE/usr/share/doc/dpm/examples/tuna.list"
find "$STAGE" -type d -exec chmod 0755 {} +

cat > "$STAGE/DEBIAN/control" <<EOF
Package: dpm
Version: $VERSION
Section: admin
Priority: optional
Architecture: $ARCH
Maintainer: dpm maintainers
Depends: libc6, dpkg, gpgv, xz-utils, gzip
Description: lightweight Debian package manager
 A C# package manager for signed Debian repositories.
EOF

dpkg-deb --root-owner-group --build "$STAGE" "$OUTPUT/dpm_${VERSION}_${ARCH}.deb"
echo "Package created: $OUTPUT/dpm_${VERSION}_${ARCH}.deb"
