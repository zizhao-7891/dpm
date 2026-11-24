#!/bin/sh
set -eu

ROOT=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
VERSION=${VERSION:-0.1.55}
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
mkdir -p "$STAGE/usr/share/dpm/locale/zh_CN/LC_MESSAGES" "$STAGE/usr/share/dpm/locale/en/LC_MESSAGES"
python3 - "$ROOT/po/dpm.zh_CN.po" "$STAGE/usr/share/dpm/locale/zh_CN/LC_MESSAGES/dpm.mo" \
    "$ROOT/po/dpm.en.po" "$STAGE/usr/share/dpm/locale/en/LC_MESSAGES/dpm.mo" <<'PY'
import struct
import sys

def parse(path):
    messages = {}
    msgid = None
    msgstr = None
    section = None
    def finish():
        nonlocal msgid, msgstr
        if msgid is not None and msgstr:
            messages[msgid] = msgstr
    for raw in open(path, encoding="utf-8"):
        line = raw.rstrip("\n")
        if line.startswith("#") or not line:
            continue
        if line.startswith("msgid "):
            finish()
            msgid = line[len("msgid "):].strip()[1:-1]
            msgstr = None
            section = "id"
        elif line.startswith("msgstr "):
            msgstr = line[len("msgstr "):].strip()[1:-1]
            section = "str"
        elif line.startswith('"') and section:
            value = line.strip()[1:-1]
            if section == "id":
                msgid += value
            else:
                msgstr = (msgstr or "") + value
    finish()
    return messages

def write_mo(messages, path):
    ids = sorted(messages)
    strs = [messages[key] for key in ids]
    ids_b = [key.encode("utf-8") for key in ids]
    strs_b = [value.encode("utf-8") for value in strs]
    n = len(ids)
    header_size = 28
    orig_table = header_size
    trans_table = orig_table + n * 8
    data_offset = trans_table + n * 8
    orig_data = b""
    orig_offsets = []
    for value in ids_b:
        orig_offsets.append((len(value), data_offset + len(orig_data)))
        orig_data += value + b"\0"
    trans_data = b""
    trans_offsets = []
    for value in strs_b:
        trans_offsets.append((len(value), data_offset + len(orig_data) + len(trans_data)))
        trans_data += value + b"\0"
    output = struct.pack("<7I", 0x950412de, 0, n, orig_table, trans_table, 0, 0)
    for length, offset in orig_offsets:
        output += struct.pack("<2I", length, offset)
    for length, offset in trans_offsets:
        output += struct.pack("<2I", length, offset)
    output += orig_data + trans_data
    open(path, "wb").write(output)

for i in range(1, len(sys.argv), 2):
    write_mo(parse(sys.argv[i]), sys.argv[i + 1])
PY
chmod 0644 "$STAGE/usr/share/dpm/locale/zh_CN/LC_MESSAGES/dpm.mo" \
    "$STAGE/usr/share/dpm/locale/en/LC_MESSAGES/dpm.mo"
find "$STAGE" -type d -exec chmod 0755 {} +

cat > "$STAGE/DEBIAN/control" <<EOF
Package: dpm
Version: $VERSION
Section: admin
Priority: optional
Architecture: $ARCH
Maintainer: dpm maintainers
Depends: libc6, dpkg, gpgv, xz-utils, gzip, bzip2, zstd, lz4
Description: lightweight Debian package manager
 A C# package manager for signed Debian repositories.
EOF

dpkg-deb --root-owner-group --build "$STAGE" "$OUTPUT/dpm_${VERSION}_${ARCH}.deb"
echo "Package created: $OUTPUT/dpm_${VERSION}_${ARCH}.deb"
