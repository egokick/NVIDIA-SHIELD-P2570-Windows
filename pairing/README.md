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
The worker waits for a supervisor acknowledgement of a supported bootstrap channel before provisioning,
then rechecks that the retained device is still the fresh selected target.

WCN Unadvise is not cancellation. Process timeout cannot prove device settings
were rolled back. Candidate credentials must remain durably available even after
a failed or uncertain operation. No USB commands or firmware updates are used.
