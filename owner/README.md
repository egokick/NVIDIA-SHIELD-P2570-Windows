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
   Connect Now SoftAP discovery provider for up to 90 seconds. It requires one enabled
   Wi-Fi adapter because that provider has no selected-adapter constraint.
2. `candidate` events contain only `target_id_hash` and `display_name`. The private
   discovery cache records model/manufacturer and physical address selection.
3. `--mode pair --target-id-hash HASH --wifi-guid GUID --uplink-guid GUID` uses
   that explicit selection. The controller must still be in push-button mode.
   It rechecks the selected WCN device's physical address, Ozmo/nVidiaBlake identity
   and push-button support. The bootstrap group must be unique, and its known
   channel must be 1–11 or 36/40/44/48/149/153/157/161/165 before any profile or
   password setter is permitted. Only channel-query error `ERROR_INVALID_STATE`
   (5023) permits an unknown channel, with the same unique group verified before
   and after the query. Other failures and timeouts prevent provisioning.

Setup uses the native WCN network, whose band Windows selects. Temporary pairing
can use 2.4 or 5 GHz; the owner verifies the selected radio, unchanged uplink and
bootstrap group under the channel-query rules above before allowing credentials to be sent. It
does not prestart a Mobile Hotspot for WCN. A pairing-network verification failure
is separate from the 5 GHz requirement for the later controller-input session.

The MT7925 laptop has created a verified 5 GHz retry network, but full discovery
attempts on channels 149 and 36 found no controller. That result verifies network
creation, not successful pairing or wireless input on this adapter.

Pairing uses a randomly generated private DIRECT SSID/passphrase and sends the
WPA2 PBKDF2-derived 64-hex `networkKey` through an anonymous stdin pipe. Credentials
never appear in arguments or event logs. The durable private candidate is written
before provisioning and retained after failure or an uncertain result. An existing
candidate is reused; a different controller or adapter choice fails explicitly.
The worker has a five-minute last-resort process deadline. There is no supported WCN
cancel or rollback: `Unadvise` removes callbacks only. The parent records uncertainty
and verifies PC cleanup independently. A `pairing-succeeded` event requires a real
ConnectSucceeded callback and durable config/provision receipts; it does not claim
that game input has been observed.

Session commands:

- `--mode import-pairing --import PRIVATE_EXPORT_PATH --wifi-guid LOCAL_GUID
  --uplink-guid LOCAL_GUID` reuses a previously verified controller pairing.
  Stage the `shield-p2570-private-pairing-export-v1` JSON in a current-user-only
  directory inside the installed app root. Import preserves the exact controller
  address, SSID and original passphrase, binds this PC's adapters, and records
  source hashes/dates in a separate schema-2 import receipt. It does not claim a
  local WCN success callback or locally verified wireless input. Conflicting saved
  or pending credentials and unresolved cleanup are rejected; file-write failure
  restores the previous config/provision bytes. The first connection still needs
  the normal actual 5 GHz channel, SSID, peer identity and input-report checks.

- `--mode validate` checks the installed package, current pairing receipt and
  read-only prerequisites.
- `--mode run --session ABSOLUTE_SESSION_DIRECTORY --parent-pid PID
  --parent-creation-filetime FILETIME` starts one temporary per-session 5 GHz
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
data host. It sends `alive\n` every 500 ms independently of Windows queries and
closes stdin before stopping its AP. One complete network validation runs at a
time, with a five-second deadline and a five-second lease after each success.
Changed network identity, an expired lease, launcher exit or explicit Stop closes
the heartbeat pipe. A stalled validator cannot renew a revoked lease. The child's
three-second lost-owner watchdog remains unchanged, and an unrequested child exit
is reported as failure even when its resource cleanup succeeds.
It restores the original global no-clients idle timeout. The public Windows API
has no per-session ownership token; exclusivity is checked before start and the
actual interface identity is checked continuously. A concurrent unrelated hotspot
publisher remains outside an OS-enforced lease. Failed or uncertain cleanup leaves
`recovery.json` and prevents another session.

`status.json` has top-level `phase`, `transport_ready`, `controller_detected`,
`connected`, `xinput_slot`, `firmware_version` and `detail`. `transport_ready`
means the receiver is listening; `controller_detected` requires the expected
controller to start or resume an accepted Ozmo session. Detection clears on
session release or child shutdown. Saved credentials alone establish neither.
The nullable firmware string is validated metadata
from the current controller's successful mailbox version reply and clears on a
new controller session, disconnect or child shutdown. It is not gamepad readiness.
`connected` requires the data host's actual pad readiness event;
Wi-Fi client counts alone are not connection proof. Event histories are bounded
to a current 1 MiB file and one previous file. Session receipts are authoritative.

Requirements for this release: Windows 11 build 26100+, Npcap with AdminOnly=0,
ViGEmBus, a suitable 5 GHz Wi-Fi adapter, and an active Internet uplink.
`--mode installation-check` checks only architecture, Windows, package integrity,
and installed drivers. It never enumerates Wi-Fi adapters or requests location
access; connection checks remain in `--mode capabilities` and pairing/session preflight.
Controller input enforces 5 GHz only. Legacy `connection.json` band preferences are ignored.
An unfinished setup retains its selected Wi-Fi and Internet adapters for retries,
provided those adapters remain available. A change to Windows' default Internet
route does not silently select a different adapter for that pending controller.
WCN discovery may use a supported 2.4 or 5 GHz group, and provisioning requires
verification of the temporary network. The `pairing-network-unavailable` checkpoint
reports a missing or ambiguous group, unsupported channel, or failed channel query
without claiming that 5 GHz hardware is absent. The later input session records
specific band-failure evidence and uses **5 GHz connection unavailable**; only an
explicit unsupported-band capability report uses **5 GHz required**. Input requests
5 GHz without a 2.4 GHz fallback. Saved 2.4 GHz bootstrap channels do not authorize
a 2.4 GHz input session. Offline operation is not supported.
The owner never changes USB drivers, Wi-Fi registry settings or persistent hotspot
credentials. Windows Mobile Hotspot can temporarily affect Internet-sharing state;
the receipt distinguishes directly verified restoration from unmeasured ICS internals.

`--mode plan` (default) and `--mode selftest` make no radio calls. Test/build outputs
belong under repository `debug/`. `pairing/build.ps1` compiles the native worker
with the static MSVC runtime and runs offline checks.
