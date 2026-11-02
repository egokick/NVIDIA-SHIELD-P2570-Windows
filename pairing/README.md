# Windows Connect Now worker

This is an original implementation against the Windows SDK. The API sequence was
checked against Microsoft's Windows Connect Now sample and documentation:

- [Windows Connect Now sample](https://github.com/microsoft/Windows-classic-samples/tree/main/Samples/Win7Samples/netds/WindowsConnectNow)
- [IWCNDevice::Connect](https://learn.microsoft.com/en-us/windows/win32/api/wcndevice/nf-wcndevice-iwcndevice-connect)
- [Push-button password contract](https://learn.microsoft.com/en-us/windows/win32/api/wcndevice/ne-wcndevice-wcn_password_type)
- [WLAN sharedKey schema](https://learn.microsoft.com/en-us/windows/win32/nativewifi/wlan-profileschema-sharedkey-security-element)

`build.ps1 -OutputDirectory <absolute path under repository debug>` finds the x64
Visual C++ toolchain with vswhere, builds C++17 with `/MT`, and runs offline tests.
Package only `wcn-pair-worker.exe`; object files and compile scripts are temporary.

Use the managed owner's setup commands. The internal worker's stdout pipe contains
physical addresses only in `private_candidate` records. The owner consumes those
records into its user-only cache; it never forwards them to UI or event history.
All profile/key material goes through stdin, never a command line or environment.
The worker waits for supervisor approval of the selected radio and unchanged uplink before provisioning,
then rechecks that the retained selected device is still available. The supervisor monitors
discovery and rejects a missing, ambiguous or unsupported bootstrap group. A unique group is
mandatory before any provisioning approval; its known channel must be
1–11 or 36/40/44/48/149/153/157/161/165. If the channel query alone returns
`ERROR_INVALID_STATE` (5023), pairing may proceed with the channel recorded as
unknown, provided the same unique group is verified before and after that query.
Other channel-query failures, including timeouts, still prevent provisioning.
The supervisor records the observed channel and group count with a specific
failure reason. An unsupported channel is distinguished from a missing
or ambiguous group and an unreadable or timed-out channel query.
The owner always verifies the actual gamepad hotspot channel and SSID after starting it,
before launching the data host. A saved pairing channel is a hint, not proof of a live channel.

The owner uses native WCN discovery and allows Windows to select a temporary
2.4 or 5 GHz network for setup. It does not prestart a Mobile Hotspot for WCN.
After pairing, the controller-input session explicitly requests and verifies
5 GHz. A 2.4 GHz pairing channel cannot authorize a 2.4 GHz input session.
On the MT7925 laptop, WCN accepted full 90-second discovery attempts on verified
5 GHz channels 149 and 36 without finding the controller. Pairing and wireless
input on that adapter remain unresolved.

WCN Unadvise is not cancellation. Process timeout cannot prove device settings
were rolled back. Candidate credentials must remain durably available even after
a failed or uncertain operation. No USB commands or firmware updates are used.

Discovery waits up to 90 seconds, and an accepted provisioning call waits up to
180 seconds for its callback, matching Microsoft's WCN sample. The owner and tray
budgets include additional startup and cleanup time; the native worker has a
five-minute last-resort lifetime bound. A timeout retains pending credentials.
