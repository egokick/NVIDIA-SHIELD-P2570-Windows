use crate::controller::Trackpad;
use anyhow::Result;
use windows::Win32::UI::Input::KeyboardAndMouse::{
    INPUT, INPUT_0, INPUT_MOUSE, MOUSE_EVENT_FLAGS, MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP,
    MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP, MOUSEEVENTF_MOVE, MOUSEEVENTF_RIGHTDOWN,
    MOUSEEVENTF_RIGHTUP, MOUSEINPUT, SendInput,
};

#[derive(Default, Clone, Copy)]
struct Motion {
    anchor: Option<(u16, u16)>,
    remainder: (f64, f64),
    trackpad_buttons: u8,
    pad_buttons: u8,
    sent_buttons: u8,
}
impl Motion {
    fn mapped_buttons(&mut self, buttons: u8) {
        self.pad_buttons = buttons & 7;
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
    sensitivity: f64,
}
impl Default for Mouse {
    fn default() -> Self {
        Self {
            motion: Motion::default(),
            gain: 4.0,
            sensitivity: 1.0,
        }
    }
}
impl Mouse {
    pub fn report(&mut self, t: Trackpad) -> Result<()> {
        // Re-measure at each gesture, including monitor switches and resolution
        // changes. Freeze during a drag so crossing a monitor never changes speed mid-swipe.
        if t.touch && self.motion.anchor.is_none() {
            if let Some(gain) = crate::display::calibration().ok().map(|c| c.default_gain) {
                self.gain = gain;
            }
        }
        let (dx, dy) = self.motion.next(t, self.gain * self.sensitivity);
        self.send(dx, dy)
    }
    pub fn mapped_buttons(&mut self, buttons: u8) -> Result<()> {
        self.motion.mapped_buttons(buttons);
        self.send(0, 0)
    }
    pub fn sensitivity(&mut self, value: f64) {
        self.sensitivity = value;
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
