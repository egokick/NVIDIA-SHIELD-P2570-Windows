This app allows the controller to connect directly to your computer over Wi-fi, without the need for an NVIDIA shield or USB dongle. 
<div align="center">
  <img src="assets/controller.png" width="80" alt="Green SHIELD controller app icon" />
  <h1>SHIELD. Untethered.</h1>
  <p>Your original <b>NVIDIA SHIELD P2570</b>, back in play.</p>
  <p><a href="https://github.com/egokick/NVIDIA-SHIELD-P2570-Windows/releases/latest/download/SHIELD-Controller-Setup.exe"><img src="assets/download-windows.svg" width="320" alt="Download for Windows" /></a></p>
  <p>Windows 11 · 24H2 or newer · x64</p>
</div>

1. **Install.** Open the download and click **Install & play**. Setup gets the drivers for you; approve the Windows and Npcap prompts when asked.
2. **Hold.** Unplug USB from the PC. Touch the green NVIDIA logo for **3 seconds** until it flashes.
3. **Play.** Click **It's flashing**. Follow the picture until the app says **Connected**. You're ready!

No accounts. No terminal commands. No dependency hunting. First-time setup needs Internet access.

<p align="center"><img src="docs/images/your-controls.png" width="880" alt="The SHIELD tray app shows Connected wirelessly, independent trackpad and joystick speed sliders, movement menus, button mappings and Reset all controls to default." /></p>

**Make it yours.** Move the cursor with the left stick or trackpad, scroll with the right stick, adjust each speed, and choose what your buttons do. [Tune your sticks](docs/joystick-controls.md), or reset everything with one click. Closing the window keeps your controller running in the tray.

Your **NVIDIA SHIELD P2570 connects directly to your computer over Wi-Fi**. The controller hardware supports **2.4 GHz and 5 GHz**; your router is not involved in this connection.

Controller input in the current Windows release **requires 5 GHz**, using channels **36, 40, 44, 48, 149, 153, 157, 161 or 165**. Windows may use 2.4 or 5 GHz for temporary discovery and pairing; after setup, the app starts and verifies a separate 5 GHz controller connection. If Windows cannot provide that connection, the app explains the requirement. [Check compatibility](docs/compatibility.md) · [Pairing picture and help](docs/guide.md)

[Linux preview and macOS status](docs/platforms.md) · [Build from source](BUILDING.md) · [All releases](https://github.com/egokick/NVIDIA-SHIELD-P2570-Windows/releases)

<sub>Independent community project. Uses Wi-Fi, not Bluetooth. Installer is currently unsigned. [GPL-2.0](LICENSE) · [Credits](THIRD-PARTY.md)</sub>
