//! Native desktop-key encoding and Windows repeat preferences.
use anyhow::Result;
use std::time::Duration;
use windows::Win32::UI::{
    Input::KeyboardAndMouse::{
        INPUT, INPUT_0, INPUT_KEYBOARD, KEYBD_EVENT_FLAGS, KEYBDINPUT, KEYEVENTF_EXTENDEDKEY,
        KEYEVENTF_KEYUP, KEYEVENTF_SCANCODE, MAPVK_VK_TO_VSC_EX, MapVirtualKeyW, SendInput,
        VIRTUAL_KEY,
    },
    WindowsAndMessaging::{
        SPI_GETKEYBOARDDELAY, SPI_GETKEYBOARDSPEED, SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS,
        SystemParametersInfoW,
    },
};
#[derive(Clone, Copy)]
pub struct Timing {
    pub delay: Duration,
    pub interval: Duration,
}
impl Timing {
    pub fn read() -> Result<Self> {
        let (mut delay, mut speed) = (0u32, 0u32);
        unsafe {
            SystemParametersInfoW(
                SPI_GETKEYBOARDDELAY,
                0,
                Some((&mut delay as *mut u32).cast()),
                SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
            )?;
            SystemParametersInfoW(
                SPI_GETKEYBOARDSPEED,
                0,
                Some((&mut speed as *mut u32).cast()),
                SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
            )?;
        }
        Ok(Self::from_settings(delay, speed))
    }
    fn from_settings(delay: u32, speed: u32) -> Self {
        Self {
            delay: Duration::from_millis(u64::from(delay.min(3) + 1) * 250),
            interval: Duration::from_secs_f64(1.0 / (2.5 + 27.5 * f64::from(speed.min(31)) / 31.0)),
        }
    }
}
impl Default for Timing {
    fn default() -> Self {
        Self::from_settings(1, 31)
    }
}
pub fn repeats(key: u16) -> bool {
    !matches!(key,0x10..=0x14|0x2c|0x5b..=0x5d|0x90..=0x91|0xa0..=0xa5|0xad..=0xb7)
}
fn encoded(key: u16, scan: u32, down: bool) -> INPUT {
    // Retain physical scan codes and extended arrows. Media and Pause use VKs.
    let physical = scan != 0 && scan >> 8 != 0xe1 && !(0xad..=0xb7).contains(&key);
    let mut flags = if physical {
        KEYEVENTF_SCANCODE
    } else {
        KEYBD_EVENT_FLAGS(0)
    };
    if scan >> 8 == 0xe0 {
        flags |= KEYEVENTF_EXTENDEDKEY;
    }
    if !down {
        flags |= KEYEVENTF_KEYUP;
    }
    INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: VIRTUAL_KEY(if physical { 0 } else { key }),
                wScan: if physical { (scan & 255) as u16 } else { 0 },
                dwFlags: flags,
                ..Default::default()
            },
        },
    }
}
pub fn emit(key: u16, down: bool) -> Result<()> {
    let scan = unsafe { MapVirtualKeyW(u32::from(key), MAPVK_VK_TO_VSC_EX) };
    let input = encoded(key, scan, down);
    if unsafe { SendInput(&[input], std::mem::size_of::<INPUT>() as i32) } != 1 {
        anyhow::bail!("keyboard_input_rejected");
    }
    Ok(())
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn extended_arrows_and_media_encode_correctly() {
        let key = unsafe { encoded(0x26, 0xe048, true).Anonymous.ki };
        assert_eq!(key.wScan, 0x48);
        assert_eq!(key.wVk, VIRTUAL_KEY(0));
        assert_eq!(key.dwFlags, KEYEVENTF_SCANCODE | KEYEVENTF_EXTENDEDKEY);
        let key = unsafe { encoded(0xb3, 0xe022, false).Anonymous.ki };
        assert_eq!(key.wVk, VIRTUAL_KEY(0xb3));
        assert_eq!(key.dwFlags, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP);
        assert!(repeats(0x41));
        assert!(!repeats(0x14));
        assert!(!repeats(0xb3));
        assert!(!repeats(0xa2));
    }
    #[test]
    fn repeat_preferences_are_bounded() {
        assert_eq!(Timing::from_settings(3, 0).delay, Duration::from_secs(1));
        assert_eq!(
            Timing::from_settings(3, 0).interval,
            Duration::from_millis(400)
        );
        assert!(Timing::from_settings(0, 31).interval < Duration::from_millis(34));
    }
}
