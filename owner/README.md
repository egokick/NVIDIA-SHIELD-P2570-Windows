# Owner and setup command contract

This executable supervises one selected controller session. It does not register
a service or start at login. Run the installed `bin/owner/ShieldControllerOwner.exe`.
The only state root is `%LOCALAPPDATA%\NVIDIA-SHIELD-P2570-Windows`; it must have
a current-user-only inheritable ACL. The installer creates that boundary.

`--mode capabilities` is read-only and emits one `capabilities` JSON event with
requirements, Wi-Fi adapters, Internet uplinks, configured choices, pairing state
and recovery state. Friendly adapter names are for this local UI only.

First pairing:

1. `--mode discover --wifi-guid GUID --uplink-guid GUID` requests the Windows
   Connect Now SoftAP discovery provider for 20 seconds. It requires one enabled
   Wi-Fi adapter because that provider has no selected-adapter constraint.
2. `candidate` events contain only `target_id_hash` and `display_name`. The private
   discovery cache records model/manufacturer and physical address selection.
3. `--mode pair --target-id-hash HASH --wifi-guid GUID --uplink-guid GUID` uses
   that explicit selection. The controller must still be in push-button mode.
   It rechecks a fresh WCN device's physical address, Ozmo/nVidiaBlake identity
   and push-button support. The actual bootstrap channel must be 1-11 or 36/40/44/48 before any
   profile or password setter is permitted.

Pairing uses a randomly generated private DIRECT SSID/passphrase and sends the
WPA2 PBKDF2-derived 64-hex `networkKey` through an anonymous stdin pipe. Credentials
never appear in arguments or event logs. The durable private candidate is written
before provisioning and retained after failure or an uncertain result. An existing
candidate is reused; a different controller or adapter choice fails explicitly.
The worker has a 95-second last-resort process deadline. There is no supported WCN
cancel or rollback: `Unadvise` removes callbacks only. The parent records uncertainty
and verifies PC cleanup independently. A `pairing-succeeded` event requires a real
ConnectSucceeded callback and durable config/provision receipts; it does not claim
that game input has been observed.

Session commands:

- `--mode validate` checks the installed package, current pairing receipt and
  read-only prerequisites.
- `--mode run --session ABSOLUTE_SESSION_DIRECTORY --parent-pid PID
  --parent-creation-filetime FILETIME` starts one temporary per-session 2.4 GHz or 5 GHz
  hotspot. Session directory is directly below `sessions/`, named 32 hex digits.
  It may contain the launcher's stdout/stderr files but no previous owner state.
- Create `request-stop` in that session directory to stop. The launcher process
  exiting also stops the session. There is no automatic duration extension or
  background service.
- `--mode recover` handles a stale recorded owner after the recorded processes
  have exited. It never stops a hotspot with a different identity. A stale setup
  marker can be cleared only after Windows already reports no active hotspot/GO.
- `--mode forget` removes only this application's config, provision receipt,
  pending candidate and discovery cache. It requires no running owner, no pending
  recovery, and verified hotspot Off. Logs remain. The tray asks for explicit
  confirmation; the controller's own firmware/settings are not changed. Use this
  before selecting a different controller or different adapters.

The owner validates the actual channel against the selected band and checks the SSID before launching its exact package
data host. It sends `alive\n` each second and closes stdin before stopping its AP.
It restores the original global no-clients idle timeout. The public Windows API
has no per-session ownership token; exclusivity is checked before start and the
actual interface identity is checked continuously. A concurrent unrelated hotspot
publisher remains outside an OS-enforced lease. Failed or uncertain cleanup leaves
`recovery.json` and prevents another session.

`status.json` has top-level `phase`, `transport_ready`, `connected`, `xinput_slot`
and `detail`. `connected` requires the data host's actual pad readiness event;
Wi-Fi client counts alone are not connection proof. Event histories are bounded
to a current 1 MiB file and one previous file. Session receipts are authoritative.

Requirements for this release: Windows 11 build 26100+, Npcap with AdminOnly=0,
ViGEmBus, a suitable 2.4 GHz or 5 GHz Wi-Fi adapter, and an active Internet uplink.
`connection.json` stores the tray's band choice separately from pairing credentials.
Automatic prefers 5 GHz; if unsupported, it chooses 2.4 GHz. RadioRestriction or
BandInterference can trigger one 2.4 GHz retry only after a verified empty hotspot.
Manual choices never switch silently. Recovery pins the actual session channel,
independently of subsequent preference changes. Offline operation is not supported.
The owner never changes USB drivers, Wi-Fi registry settings or persistent hotspot
credentials. Windows Mobile Hotspot can temporarily affect Internet-sharing state;
the receipt distinguishes directly verified restoration from unmeasured ICS internals.

`--mode plan` (default) and `--mode selftest` make no radio calls. Test/build outputs
belong under repository `debug/`. `pairing/build.ps1` compiles the native worker
with the static MSVC runtime and runs offline checks.
