# Make the sticks feel right

The left stick moves the Windows cursor by default. The right stick scrolls up and down like a mouse wheel; push farther to scroll faster. Horizontal tilt does not scroll. Tilt a little for a small adjustment, or farther to travel faster. Each stick has its own **Movement** menu and **Speed** slider. Pressing a stick is a separate button mapping.

Choose **Cursor**, **Scroll up / down**, **Arrow keys**, **WASD**, or **Gamepad only** for each stick. Gamepad only stops that stick's desktop action; games still receive its normal controller input. You can also choose **Cursor** for the D-pad directions. Its default remains the keyboard arrow keys.

Start with **100% speed**, **Precise**, **20% dead zone**, and **Smoothing off**. Open **Feel & drift** to find these options; **Ignore tiny movements** is the dead-zone slider. These are this app's starting defaults, not a measurement of your controller's wear or an optimum established by user testing. The speed baseline uses the display's resolution and physical size, or a size estimate when unavailable. Windows pointer settings also affect the final movement.

| If this happens | Try this |
|---|---|
| Crossing the screen is too slow or fast | Adjust that stick's **Speed** slider. |
| Small targets are hard to select | Keep **Precise** and lower speed a little. |
| You prefer an even response throughout the stick's travel | Choose **Linear**. |
| You want more movement from a small tilt | Choose **Quick**. |
| The cursor moves when you let go | Increase the **Dead zone** slightly, until it stays still. |
| You have to push too far before anything happens | Lower the **Dead zone**, stopping before drift appears. |
| Movement is jittery while you are deliberately holding the stick | Try **Light** smoothing, then **Steady** if needed. Return to **Off** if it feels delayed. |

**Reset all controls to default** restores both sticks, trackpad speed and button mappings together. Existing custom button mappings are kept when adding these stick settings.

## What the movement choices mean

**Response curve** controls how stick tilt becomes cursor speed. Precise gives small tilts more room for careful aiming; Linear increases speed evenly; Quick reaches higher speeds sooner. The maximum speed is controlled separately. Microsoft demonstrates a circular dead zone with the remaining travel rescaled, and discusses nonlinear curves for finer low-range control. This app applies that approach to desktop pointing. [Microsoft XInput guidance](https://learn.microsoft.com/en-us/windows/win32/xinput/getting-started-with-xinput#dead-zone).

**Smoothing** filters changing movement, which can reduce jitter but also make direction changes feel delayed. Valve documents curves and smoothing as separate controls; the original 1€ filter research explains the jitter-versus-lag tradeoff. Our recommendation is therefore **Off** first, adding only as much filtering as helps your hand and controller. The optional Light and Steady settings use this app's simple time-based filter, not the 1€ algorithm. [Valve input modes](https://partner.steamgames.com/doc/features/steam_controller/input_source_modes), [Casiez, Roussel and Vogel's filtering research](https://gery.casiez.net/1euro/).

**Interpolation and acceleration** are different from the response curve. This app turns the current tilt into movement over elapsed time, so holding the stick keeps moving at a steady rate without needing repeated identical reports. It does not predict future input or gradually accelerate just because you keep holding the stick. Smoothing is optional, and returning to the dead zone stops movement immediately instead of letting it coast. Diagonal movement is bounded so it does not gain an unintended speed boost.

The settings change desktop actions only. Choose Gamepad only if you do not want a stick to move both the cursor and your character in a game that accepts both kinds of input.
