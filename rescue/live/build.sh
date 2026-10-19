#!/bin/bash
# Builds the TechSource Rescue USB image (a Debian 13 live system with DiskImager Rescue).
# Runs as root inside a debian:trixie container (see .github/workflows/rescue-image.yml):
#   docker run --rm --privileged -v "$PWD":/w -w /w debian:trixie bash rescue/live/build.sh /w/out
# Expects the published Linux rescue app in rescue/live/config/includes.chroot/opt/diskimager/.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/out}"
VERSION="${RESCUE_VERSION:-dev}"
mkdir -p "$OUT"

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install -y --no-install-recommends live-build ca-certificates curl gnupg imagemagick fonts-dejavu-core xorriso \
    squashfs-tools dosfstools mtools grub-efi-amd64-bin grub-pc-bin shim-signed grub-efi-amd64-signed syslinux isolinux syslinux-common

# Check every package in the lists exists in trixie up front, so one run reports them all.
sed -i 's/^Components: main$/Components: main contrib non-free non-free-firmware/' /etc/apt/sources.list.d/debian.sources
apt-get update -qq
MISSING=""
for p in $(grep -hv '^\s*#' "$HERE"/config/package-lists/*.list.chroot); do
    apt-cache show "$p" >/dev/null 2>&1 || MISSING="$MISSING $p"
done
if [ -n "$MISSING" ]; then echo "These packages don't exist in Debian trixie:$MISSING"; exit 1; fi

test -x "$HERE/config/includes.chroot/opt/diskimager/diskimager-rescue" || { echo "The rescue app isn't in config/includes.chroot/opt/diskimager"; exit 1; }
echo "$VERSION" > "$HERE/config/includes.chroot/opt/diskimager/VERSION"

# Wallpaper and menu icon in TechSource colours.
ART="$HERE/config/includes.chroot/usr/share/backgrounds/techsource"
mkdir -p "$ART" "$HERE/config/includes.chroot/usr/share/pixmaps"
cp "$HERE/../../logo.png" "$HERE/config/includes.chroot/usr/share/pixmaps/diskimager.png"
IM=convert; command -v magick >/dev/null && IM=magick
$IM -size 1920x1080 radial-gradient:'#1E40AF'-'#0B1B3A' \
    \( "$HERE/../../logo.png" -resize 120x120 \) -gravity southeast -geometry +60+110 -composite \
    -font DejaVu-Sans -fill '#BFDBFE' -pointsize 30 -gravity southeast -annotate +60+60 'TechSource Rescue' \
    "$ART/wallpaper.png" || $IM -size 1920x1080 radial-gradient:'#1E40AF'-'#0B1B3A' "$ART/wallpaper.png"

WORK=/tmp/tsr-build
rm -rf "$WORK"; mkdir -p "$WORK"; cd "$WORK"
lb config \
    --mode debian --distribution trixie --architectures amd64 \
    --archive-areas "main contrib non-free non-free-firmware" \
    --binary-images iso-hybrid \
    --bootloaders "grub-efi,syslinux" --uefi-secure-boot enable \
    --debian-installer none --memtest none \
    --apt-recommends false --firmware-chroot true --firmware-binary false \
    --chroot-squashfs-compression-type xz \
    --iso-volume "TS-RESCUE" --iso-application "TechSource Rescue" --iso-publisher "TechSource Digital" \
    --bootappend-live "boot=live components quiet splash username=tech user-fullname=Technician hostname=ts-rescue locales=en_US.UTF-8 keyboard-layouts=us persistence persistence-encryption=luks,none"
cp -r "$HERE/config/." config/
chmod +x config/hooks/live/*.hook.chroot config/includes.chroot/usr/local/bin/* config/includes.chroot/usr/local/sbin/* config/includes.chroot/opt/diskimager/diskimager-rescue
lb build

# Sanity checks on the finished system: things that only show up at boot otherwise.
for f in usr/lib/user-setup/user-setup-apply usr/lib/live/config/0030-user-setup etc/lightdm/lightdm.conf.d/50-techsource.conf \
         usr/share/xsessions/xfce.desktop opt/diskimager/diskimager-rescue usr/bin/tailscale usr/bin/firefox-esr; do
    test -e "chroot/$f" || { echo "Missing from the image: /$f"; exit 1; }
done

ISO=$(ls -1 *.iso | head -1)
mv "$ISO" "$OUT/TechSource-Rescue-$VERSION.iso"
ls -l "$OUT"
SIZE=$(stat -c %s "$OUT/TechSource-Rescue-$VERSION.iso")
echo "Image size: $((SIZE / 1048576)) MB"
if [ "$SIZE" -ge 2147483648 ]; then echo "The image is over 2 GB, which GitHub releases don't accept."; exit 1; fi
