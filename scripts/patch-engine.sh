#!/usr/bin/env bash
# Overlays SRK VPN onto the ics-openvpn "skeleton" flavor and sets the app id.
set -euo pipefail
ENGINE="$1"            # path to ics-openvpn checkout
HERE="$(cd "$(dirname "$0")/.." && pwd)"
SK="$ENGINE/main/src/skeleton"

rm -f "$SK/AndroidManifest.xml"
cp -r "$HERE/overlay/skeleton/." "$SK/"

G="$ENGINE/main/build.gradle.kts"
python3 - "$G" <<'PY'
import re, sys
p = sys.argv[1]; s = open(p).read()
s = s.replace('minSdk = 23',
  'applicationId = "in.shazverse.srkvpn"\n        minSdk = 23\n        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a") }', 1)
s = re.sub(r'versionName = "[^"]*"', 'versionName = "1.0"', s, count=1)
s = re.sub(r'versionNameSuffix = "-o2"', 'versionNameSuffix = ""', s, count=1)
open(p, 'w').write(s)
PY
grep -n 'applicationId\|abiFilters\|versionName' "$G"

# Packaging the native OpenVPN/OpenSSL libraries needs more than Gradle's default 512 MB heap.
printf '\norg.gradle.jvmargs=-Xmx6g -XX:MaxMetaspaceSize=1g -Dfile.encoding=UTF-8\n' >> "$ENGINE/gradle.properties"
