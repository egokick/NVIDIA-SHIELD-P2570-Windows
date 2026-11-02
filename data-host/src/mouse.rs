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
    buttons: u8,
}
impl Motion {
    fn next(&mut self, t: Trackpad) -> (i32, i32, u8, u8) {
        let delta = if t.touch {
            self.anchor
                .map(|(x, y)| {
                    (
                        ((i32::from(t.x) - i32::from(x)).clamp(-50, 50)) * 2,
                        ((i32::from(t.y) - i32::from(y)).clamp(-50, 50)) * 2,
                    )
                })
                .unwrap_or((0, 0))
        } else {
            (0, 0)
        };
        self.anchor = t.touch.then_some((t.x, t.y));
        let changed = self.buttons ^ (t.buttons & 7);
        self.buttons = t.buttons & 7;
        (delta.0, delta.1, changed, self.buttons)
    }
}
#[derive(Default)]
pub struct Mouse {
    motion: Motion,
}
impl Mouse {
    pub fn report(&mut self, t: Trackpad) -> Result<()> {
        let before = self.motion;
        let (dx, dy, changed, buttons) = self.motion.next(t);
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
        if flags.0 != 0 {
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
            if unsafe { SendInput(&[input], std::mem::size_of::<INPUT>() as i32) } != 1 {
                self.motion = before;
                anyhow::bail!("mouse_input_rejected");
            }
        }
        Ok(())
    }
    pub fn release(&mut self) -> Result<()> {
        self.report(Trackpad::default())
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
    fn touch_lift_and_retouch_never_jump() {
        let mut m = Motion::default();
        let t = |x, y, touch| Trackpad {
            x,
            y,
            touch,
            buttons: 0,
        };
        assert_eq!(m.next(t(100, 100, true)), (0, 0, 0, 0));
        assert_eq!(m.next(t(110, 95, true)), (20, -10, 0, 0));
        assert_eq!(m.next(t(0, 0, false)), (0, 0, 0, 0));
        assert_eq!(m.next(t(500, 500, true)), (0, 0, 0, 0));
    }
    #[test]
    fn buttons_have_edges_and_release() {
        let mut m = Motion::default();
        let t = Trackpad {
            buttons: 7,
            ..Default::default()
        };
        assert_eq!(m.next(t), (0, 0, 7, 7));
        assert_eq!(m.next(t), (0, 0, 0, 7));
        assert_eq!(m.next(Trackpad::default()), (0, 0, 7, 0));
    }
    #[test]
    fn corrupt_jump_is_bounded() {
        let mut m = Motion::default();
        m.next(Trackpad {
            touch: true,
            ..Default::default()
        });
        assert_eq!(
            m.next(Trackpad {
                touch: true,
                x: 65535,
                y: 65535,
                buttons: 0
            }),
            (100, 100, 0, 0)
        );
    }
}
