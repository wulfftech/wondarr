#!/bin/sh
#
# Downloads a pinned slskd release and unpacks it into <dest dir>.
#
#   scripts/fetch-slskd.sh <version> <arch: x64|arm64> <dest dir>
#
# slskd is AGPL-3.0. Compilarr does not link against it or copy its code (ADR-0004): the released
# binary runs as a supervised child process, so the licence notice has to travel with it into the
# image. Only releases listed in the table below are accepted — the checksum is the pin.

set -eu

if [ "$#" -ne 3 ]; then
    echo "usage: $0 <version> <arch: x64|arm64> <dest dir>" >&2
    exit 1
fi

version=$1
arch=$2
dest=$3

case "$version" in
    0.26.0)
        case "$arch" in
            x64)
                sha256=9c19c04767ef036a47716404d097433e23fbdb41b339e0e8cbc2329c98b22583
                ;;
            arm64)
                sha256=57d4b9dbb0ad34aa6e6aaaf79b0bf3347dec5efcb48ad2b12f9ed4ece42787aa
                ;;
            *)
                echo "fetch-slskd: unknown architecture '$arch' (expected x64 or arm64)" >&2
                exit 1
                ;;
        esac
        ;;
    *)
        echo "fetch-slskd: no pinned release for slskd version '$version'" >&2
        exit 1
        ;;
esac

asset="slskd-$version-linux-$arch.zip"
url="https://github.com/slskd/slskd/releases/download/$version/$asset"

workdir=$(mktemp -d)
trap 'rm -rf "$workdir"' EXIT HUP INT TERM

echo "fetch-slskd: downloading $url"
if command -v curl >/dev/null 2>&1; then
    curl -fsSL -o "$workdir/$asset" "$url"
elif command -v wget >/dev/null 2>&1; then
    wget -q -O "$workdir/$asset" "$url"
else
    echo "fetch-slskd: neither curl nor wget is available" >&2
    exit 1
fi

echo "$sha256  $workdir/$asset" | sha256sum -c -

unzip -q -o "$workdir/$asset" -d "$workdir/unpacked"

if [ ! -f "$workdir/unpacked/LICENSE" ]; then
    echo "fetch-slskd: $asset does not contain the LICENSE file the AGPL requires" >&2
    exit 1
fi

mkdir -p "$dest"
cp -R "$workdir/unpacked/." "$dest/"

chmod 0755 "$dest/slskd"

# slskd is AGPL-3.0: keep the notice next to the binary we redistribute.
cp "$workdir/unpacked/LICENSE" "$dest/LICENSE"

echo "fetch-slskd: slskd $version ($arch) unpacked into $dest"
