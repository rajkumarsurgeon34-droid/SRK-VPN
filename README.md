# SRK VPN

SRK VPN changes your IP address to another country using free **VPN Gate** servers
(a public relay network run by volunteers and the University of Tsukuba, Japan).

- Country list with flags, server count and speed; tap a country to connect to its best server
- Automatic fallback: if a server doesn't answer within 25 s the app tries the next one (up to 6)
- Pick an exact server, live traffic counters, and "Check IP" to confirm your new country
- OpenVPN engine: [ics-openvpn](https://github.com/schwabe/ics-openvpn) (GPLv2), built from its `skeleton` flavor

## Build

Every push to `main` runs **Actions → Build APK** and publishes `SRK-VPN.apk` under **Releases**.

The workflow clones ics-openvpn at `ENGINE_TAG`, copies `overlay/skeleton/` into
`main/src/skeleton/` (`scripts/patch-engine.sh`), sets the app id `in.shazverse.srkvpn`,
and builds `:main:assembleSkeletonOvpn2Release`. `signing/srk-vpn-debug.keystore` keeps
the signature the same between builds so updates install over the previous version.

## Files

- `overlay/skeleton/java/in/shazverse/srkvpn/MainActivity.java` — screen, VPN permission, connect/disconnect, IP check
- `overlay/skeleton/java/in/shazverse/srkvpn/VpnGate.java` — downloads and parses the VPN Gate server list
- `overlay/skeleton/assets/www/index.html` — the user interface

## Privacy

VPN Gate relays are run by volunteers who can see unencrypted traffic, and VPN Gate keeps
connection logs. Use it to change your country, not for banking or passwords.

## Licence

Because it includes ics-openvpn, SRK VPN is distributed under the GNU GPL v2.
