use crate::controller::{Pad, Trackpad, btn};
use anyhow::Result;
use windows::Win32::{
    Foundation::POINT,
    Graphics::Gdi::{GetMonitorInfoW, MONITOR_DEFAULTTONEAREST, MONITORINFO, MonitorFromPoint},
    UI::{
        HiDpi::{DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, SetThreadDpiAwarenessContext},
        Input::KeyboardAndMouse::{
            INPUT, INPUT_0, INPUT_MOUSE, MOUSE_EVENT_FLAGS, MOUSEEVENTF_LEFTDOWN,
            MOUSEEVENTF_LEFTUP, MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, MOUSEEVENTF_MOVE,
            MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP, MOUSEINPUT, SendInput,
        },
        WindowsAndMessaging::GetCursorPos,
    },
};

// One gain for both axes preserves direction. Work in physical pixels under a
// per-monitor DPI context, never multiply by DPI again. The geometric mean of
// the current monitor's dimensions avoids over-speeding ultrawide desktops.
fn display_gain(width: i32, height: i32) -> Option<f64> {
    (width > 0 && height > 0).then(|| {
        4.0 * ((f64::from(width) * f64::from(height) / (1920.0 * 1080.0)).sqrt()).clamp(0.75, 3.0)
    })
}

fn current_display_gain() -> Option<f64> {
    unsafe {
        let previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        if previous.0.is_null() {
            return None;
        }
        let mut point = POINT::default();
        let mut info = MONITORINFO {
            cbSize: std::mem::size_of::<MONITORINFO>() as u32,
            ..Default::default()
        };
        let gain = if GetCursorPos(&mut point).is_ok()
            && GetMonitorInfoW(MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST), &mut info)
                .as_bool()
        {
            display_gain(
                info.rcMonitor.right - info.rcMonitor.left,
                info.rcMonitor.bottom - info.rcMonitor.top,
            )
        } else {
            None // A locked/unavailable desktop leaves the last measured gain intact.
        };
        SetThreadDpiAwarenessContext(previous);
        gain
    }
}

#[derive(Default, Clone, Copy)]
struct Motion {
    anchor: Option<(u16, u16)>,
    remainder: (f64, f64),
    trackpad_buttons: u8,
    pad_buttons: u8,
    sent_buttons: u8,
}
impl Motion {
    fn pad(&mut self, p: &Pad) {
        self.pad_buttons =
            u8::from(p.buttons & btn::A != 0) | (u8::from(p.buttons & btn::B != 0) << 1);
    }
    fn next(&mut self, t: Trackpad, gain: f64) -> (i32, i32) {
        let delta = if t.touch {
            self.anchor
                .map(|(x, y)| {
                    let x = f64::from((i32::from(t.x) - i32::from(x)).clamp(-50, 50)) * gain
                        + self.remainder.0;
                    let y = f64::from((i32::from(t.y) - i32::from(y)).clamp(-50, 50)) * gain
                        + self.remainder.1;
                    self.remainder = (x.fract(), y.fract());
                    (x.trunc() as i32, y.trunc() as i32)
                })
                .unwrap_or((0, 0))
        } else {
            self.remainder = (0.0, 0.0);
            (0, 0)
        };
        self.anchor = t.touch.then_some((t.x, t.y));
        self.trackpad_buttons = t.buttons & 7;
        delta
    }
    fn buttons(&self) -> u8 {
        self.trackpad_buttons | self.pad_buttons
    }
    fn flags(&self, dx: i32, dy: i32) -> MOUSE_EVENT_FLAGS {
        let buttons = self.buttons();
        let changed = self.sent_buttons ^ buttons;
        let mut flags = if dx != 0 || dy != 0 {
            MOUSEEVENTF_MOVE
        } else {
            MOUSE_EVENT_FLAGS(0)
        };
        for (bit, down, up) in [
            (1, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
            (2, MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            (4, MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
        ] {
            if changed & bit != 0 {
                flags |= if buttons & bit != 0 { down } else { up };
            }
        }
        flags
    }
    fn commit(&mut self) {
        self.sent_buttons = self.buttons();
    }
}

pub struct Mouse {
    motion: Motion,
    gain: f64,
}
impl Default for Mouse {
    fn default() -> Self {
        Self {
            motion: Motion::default(),
            gain: 4.0,
        }
    }
}
impl Mouse {
    pub fn report(&mut self, t: Trackpad) -> Result<()> {
        // Re-measure at each gesture, including monitor switches and resolution
        // changes. Freeze during a drag so crossing a monitor never changes speed mid-swipe.
        if t.touch && self.motion.anchor.is_none() {
            if let Some(gain) = current_display_gain() {
                self.gain = gain;
            }
        }
        let (dx, dy) = self.motion.next(t, self.gain);
        self.send(dx, dy)
    }
    pub fn pad(&mut self, p: &Pad) -> Result<()> {
        self.motion.pad(p);
        self.send(0, 0)
    }
    fn send(&mut self, dx: i32, dy: i32) -> Result<()> {
        let flags = self.motion.flags(dx, dy);
        if flags.0 == 0 {
            return Ok(());
        }
        let input = INPUT {
            r#type: INPUT_MOUSE,
            Anonymous: INPUT_0 {
                mi: MOUSEINPUT {
                    dx,
                    dy,
                    dwFlags: flags,
                    ..Default::default()
                },
            },
        };
        // Update delivered state only after success. Cleanup can still release
        // a previously delivered click if a later event is blocked by Windows.
        if unsafe { SendInput(&[input], std::mem::size_of::<INPUT>() as i32) } != 1 {
            anyhow::bail!("mouse_input_rejected");
        }
        self.motion.commit();
        Ok(())
    }
    pub fn release(&mut self) -> Result<()> {
        self.motion.anchor = None;
        self.motion.remainder = (0.0, 0.0);
        self.motion.trackpad_buttons = 0;
        self.motion.pad_buttons = 0;
        self.send(0, 0)
    }
}
impl Drop for Mouse {
    fn drop(&mut self) {
        let _ = self.release();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn real_pad_a_and_b_map_to_clicks_with_release_edges() {
        let mut motion = Motion::default();
        for (buttons, expected) in [
            (btn::A, MOUSEEVENTF_LEFTDOWN),
            (btn::A | btn::B, MOUSEEVENTF_RIGHTDOWN),
            (btn::B, MOUSEEVENTF_LEFTUP),
            (
                btn::X | btn::Y | btn::SPURIOUS | btn::START,
                MOUSEEVENTF_RIGHTUP,
            ),
            (0, MOUSE_EVENT_FLAGS(0)),
        ] {
            motion.pad(&Pad {
                buttons,
                ..Pad::default()
            });
            assert_eq!(motion.flags(0, 0), expected);
            motion.commit();
        }
    }
    fn touch(x: u16, y: u16) -> Trackpad {
        Trackpad {
            touch: true,
            x,
            y,
            ..Default::default()
        }
    }
    #[test]
    fn lift_and_retouch_never_jump() {
        let mut m = Motion::default();
        assert_eq!(m.next(touch(100, 100), 2.0), (0, 0));
        assert_eq!(m.next(touch(110, 95), 2.0), (20, -10));
        assert_eq!(m.next(Trackpad::default(), 2.0), (0, 0));
        assert_eq!(m.next(touch(500, 500), 4.0), (0, 0));
    }
    #[test]
    fn click_sources_share_ownership_for_dragging() {
        let mut m = Motion::default();
        m.pad_buttons = 1;
        assert_eq!(m.flags(0, 0), MOUSEEVENTF_LEFTDOWN);
        m.commit();
        m.trackpad_buttons = 1;
        assert_eq!(m.flags(0, 0).0, 0);
        m.pad_buttons = 0;
        assert_eq!(m.flags(0, 0).0, 0);
        m.trackpad_buttons = 0;
        assert_eq!(m.flags(0, 0), MOUSEEVENTF_LEFTUP);
    }
    #[test]
    fn right_middle_and_failed_release_keep_correct_edges() {
        let mut m = Motion::default();
        m.pad_buttons = 2;
        m.trackpad_buttons = 4;
        assert_eq!(
            m.flags(0, 0),
            MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_MIDDLEDOWN
        );
        m.commit();
        m.pad_buttons = 0;
        m.trackpad_buttons = 0;
        let up = MOUSEEVENTF_RIGHTUP | MOUSEEVENTF_MIDDLEUP;
        assert_eq!(m.flags(0, 0), up);
        assert_eq!(m.flags(0, 0), up); // rejected send did not commit
        m.commit();
        assert_eq!(m.flags(0, 0).0, 0);
    }
    #[test]
    fn physical_resolution_scales_once_and_bounds_extremes() {
        assert_eq!(display_gain(1920, 1080), Some(4.0));
        assert_eq!(display_gain(3840, 2160), Some(8.0));
        assert_eq!(display_gain(1080, 1920), Some(4.0));
        assert_eq!(display_gain(0, 1080), None);
        assert_eq!(display_gain(20000, 20000), Some(12.0));
        assert_eq!(display_gain(640, 480), Some(3.0));
        assert!((display_gain(2560, 1440).unwrap() - 16.0 / 3.0).abs() < 1e-10);
    }
    #[test]
    fn fractional_motion_accumulates_in_both_directions() {
        let mut m = Motion::default();
        m.next(touch(100, 100), 1.5);
        assert_eq!(m.next(touch(101, 99), 1.5), (1, -1));
        assert_eq!(m.next(touch(102, 98), 1.5), (2, -2));
    }
    #[test]
    fn corrupt_jump_is_bounded_at_maximum_gain() {
        let mut m = Motion::default();
        m.next(touch(0, 0), 12.0);
        assert_eq!(m.next(touch(65535, 65535), 12.0), (600, 600));
    }
}
