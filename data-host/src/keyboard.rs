//! Desktop keys derived from the paired controller. XInput remains independent.
use crate::controller::{Pad, key};
use anyhow::Result;
use std::time::{Duration, Instant};
use windows::Win32::UI::Input::KeyboardAndMouse::{
    INPUT, INPUT_0, INPUT_KEYBOARD, KEYBDINPUT, KEYEVENTF_EXTENDEDKEY, KEYEVENTF_KEYUP,
    KEYEVENTF_SCANCODE, SendInput, VIRTUAL_KEY,
};
use windows::Win32::UI::WindowsAndMessaging::{
    SPI_GETKEYBOARDDELAY, SPI_GETKEYBOARDSPEED, SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS,
    SystemParametersInfoW,
};

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Key {
    Up,
    Right,
    Down,
    Left,
    VolumeUp,
    VolumeDown,
}
const KEYS: [Key; 6] = [
    Key::Up,
    Key::Right,
    Key::Down,
    Key::Left,
    Key::VolumeUp,
    Key::VolumeDown,
];

fn wanted(p: &Pad) -> [bool; 6] {
    [
        matches!(p.hat, 0 | 1 | 7),
        matches!(p.hat, 1 | 2 | 3),
        matches!(p.hat, 3 | 4 | 5),
        matches!(p.hat, 5 | 6 | 7),
        p.keys & key::VOL_UP != 0,
        p.keys & key::VOL_DOWN != 0,
    ]
}

#[derive(Clone, Copy)]
struct Timing {
    delay: Duration,
    interval: Duration,
}
impl Timing {
    fn from_settings(delay: u32, speed: u32) -> Self {
        Self {
            delay: Duration::from_millis(u64::from(delay.min(3) + 1) * 250),
            interval: Duration::from_secs_f64(1.0 / (2.5 + 27.5 * f64::from(speed.min(31)) / 31.0)),
        }
    }
    fn read() -> Result<Self> {
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
    fn interval_for(self, key: Key) -> Duration {
        if matches!(key, Key::VolumeUp | Key::VolumeDown) {
            self.interval.max(Duration::from_millis(100))
        } else {
            self.interval
        }
    }
}

#[derive(Default)]
struct State {
    wanted: [bool; 6],
    held: [bool; 6],
    repeat_at: [Option<Instant>; 6],
}
impl State {
    fn report(
        &mut self,
        wanted: [bool; 6],
        now: Instant,
        timing: Timing,
        mut emit: impl FnMut(Key, bool) -> Result<()>,
    ) -> Result<()> {
        self.wanted = wanted;
        let mut result = Ok(());
        // Release old directions first, so Up -> Down never momentarily holds both.
        for (i, key) in KEYS.into_iter().enumerate() {
            if !wanted[i] {
                self.repeat_at[i] = None;
                if self.held[i] {
                    match emit(key, false) {
                        Ok(()) => self.held[i] = false,
                        Err(error) => {
                            if result.is_ok() {
                                result = Err(error);
                            }
                        }
                    }
                }
            }
        }
        // Do not add new held keys if any preceding key-up was rejected.
        if result.is_err() {
            return result;
        }
        for (i, key) in KEYS.into_iter().enumerate() {
            if wanted[i] && !self.held[i] {
                emit(key, true)?;
                self.held[i] = true;
                self.repeat_at[i] = Some(now + timing.delay);
            }
        }
        Ok(())
    }
    fn tick(
        &mut self,
        now: Instant,
        timing: Timing,
        mut emit: impl FnMut(Key, bool) -> Result<()>,
    ) -> Result<()> {
        for (i, key) in KEYS.into_iter().enumerate() {
            if self.wanted[i] && self.held[i] && self.repeat_at[i].is_some_and(|due| now >= due) {
                emit(key, true)?;
                // No catch-up burst after a delayed loop: at most one repeat per key per tick.
                self.repeat_at[i] = Some(now + timing.interval_for(key));
            }
        }
        Ok(())
    }
    fn release(&mut self, mut emit: impl FnMut(Key, bool) -> Result<()>) -> Result<()> {
        self.wanted = [false; 6];
        self.repeat_at = [None; 6];
        let mut result = Ok(());
        for (i, key) in KEYS.into_iter().enumerate() {
            if self.held[i] {
                match emit(key, false) {
                    Ok(()) => self.held[i] = false,
                    Err(error) => {
                        if result.is_ok() {
                            result = Err(error);
                        }
                    }
                }
            }
        }
        result
    }
}

fn input(key: Key, down: bool) -> Option<INPUT> {
    let (virtual_key, scan, mut flags) = match key {
        Key::Up => (
            VIRTUAL_KEY(0),
            0x48,
            KEYEVENTF_SCANCODE | KEYEVENTF_EXTENDEDKEY,
        ),
        Key::Right => (
            VIRTUAL_KEY(0),
            0x4d,
            KEYEVENTF_SCANCODE | KEYEVENTF_EXTENDEDKEY,
        ),
        Key::Down => (
            VIRTUAL_KEY(0),
            0x50,
            KEYEVENTF_SCANCODE | KEYEVENTF_EXTENDEDKEY,
        ),
        Key::Left => (
            VIRTUAL_KEY(0),
            0x4b,
            KEYEVENTF_SCANCODE | KEYEVENTF_EXTENDEDKEY,
        ),
        Key::VolumeUp | Key::VolumeDown => return None,
    };
    if !down {
        flags |= KEYEVENTF_KEYUP;
    }
    Some(INPUT {
        r#type: INPUT_KEYBOARD,
        Anonymous: INPUT_0 {
            ki: KEYBDINPUT {
                wVk: virtual_key,
                wScan: scan,
                dwFlags: flags,
                ..Default::default()
            },
        },
    })
}
fn emit(key: Key, down: bool) -> Result<()> {
    let Some(input) = input(key, down) else {
        if down {
            crate::volume::adjust(key == Key::VolumeUp);
        }
        return Ok(());
    };
    // A single event makes acceptance atomic for our held-state bookkeeping.
    if unsafe { SendInput(&[input], std::mem::size_of::<INPUT>() as i32) } != 1 {
        anyhow::bail!("keyboard_input_rejected");
    }
    Ok(())
}

#[derive(Default)]
pub struct Keys {
    state: State,
    timing: Option<Timing>,
}
impl Keys {
    pub fn report(&mut self, p: &Pad, now: Instant) -> Result<()> {
        let desired = wanted(p);
        // Neutral gamepad reports and construction never read or inject desktop input.
        if self.timing.is_none() && !desired.into_iter().any(|pressed| pressed) {
            return Ok(());
        }
        let timing = match self.timing {
            Some(timing) => timing,
            None => {
                let timing = Timing::read()?;
                self.timing = Some(timing);
                timing
            }
        };
        self.state.report(desired, now, timing, emit)
    }
    pub fn tick(&mut self, now: Instant) -> Result<()> {
        if let Some(timing) = self.timing {
            self.state.tick(now, timing, emit)?;
        }
        Ok(())
    }
    pub fn release(&mut self) -> Result<()> {
        self.state.release(emit)
    }
}
impl Drop for Keys {
    fn drop(&mut self) {
        let _ = self.release();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn timing() -> Timing {
        Timing {
            delay: Duration::from_millis(500),
            interval: Duration::from_millis(40),
        }
    }
    fn pad(hat: u8, keys: u8) -> Pad {
        Pad {
            hat,
            keys,
            ..Pad::default()
        }
    }
    #[test]
    fn diagonals_transition_with_edges_and_ignore_other_controls() {
        let mut state = State::default();
        let now = Instant::now();
        let mut events = Vec::new();
        for p in [pad(1, 0), pad(1, 0), pad(3, 0), Pad::default()] {
            state
                .report(wanted(&p), now, timing(), |key, down| {
                    events.push((key, down));
                    Ok(())
                })
                .unwrap();
        }
        assert_eq!(
            events,
            [
                (Key::Up, true),
                (Key::Right, true),
                (Key::Up, false),
                (Key::Down, true),
                (Key::Right, false),
                (Key::Down, false)
            ]
        );
        assert_eq!(
            wanted(&Pad {
                buttons: u16::MAX,
                keys: key::MUTE | key::NVIDIA | key::BACK | key::HOME,
                ..Pad::default()
            }),
            [false; 6]
        );
    }
    #[test]
    fn all_hat_positions_and_neutral_values() {
        let expected = [
            [true, false, false, false],
            [true, true, false, false],
            [false, true, false, false],
            [false, true, true, false],
            [false, false, true, false],
            [false, false, true, true],
            [false, false, false, true],
            [true, false, false, true],
        ];
        for (hat, expected) in expected.into_iter().enumerate() {
            assert_eq!(&wanted(&pad(hat as u8, 0))[..4], &expected);
        }
        for hat in 8..=15 {
            assert_eq!(wanted(&pad(hat, 0)), [false; 6]);
        }
    }
    #[test]
    fn held_keys_repeat_on_timer_not_report_frequency_and_no_catchup_burst() {
        let mut state = State::default();
        let now = Instant::now();
        state
            .report(wanted(&pad(0, key::VOL_UP)), now, timing(), |_, _| Ok(()))
            .unwrap();
        state
            .report(
                wanted(&pad(0, key::VOL_UP)),
                now + Duration::from_millis(450),
                timing(),
                |_, _| panic!("unchanged report must not emit"),
            )
            .unwrap();
        state
            .tick(now + Duration::from_millis(499), timing(), |_, _| {
                panic!("too early")
            })
            .unwrap();
        let mut events = Vec::new();
        state
            .tick(now + Duration::from_millis(500), timing(), |key, down| {
                events.push((key, down));
                Ok(())
            })
            .unwrap();
        assert_eq!(events, [(Key::Up, true), (Key::VolumeUp, true)]);
        events.clear();
        state
            .tick(now + Duration::from_millis(540), timing(), |key, down| {
                events.push((key, down));
                Ok(())
            })
            .unwrap();
        assert_eq!(events, [(Key::Up, true)]);
        events.clear();
        state
            .tick(now + Duration::from_secs(20), timing(), |key, down| {
                events.push((key, down));
                Ok(())
            })
            .unwrap();
        assert_eq!(events, [(Key::Up, true), (Key::VolumeUp, true)]);
    }
    #[test]
    fn release_stops_all_repeats_and_is_idempotent() {
        let mut state = State::default();
        let now = Instant::now();
        state
            .report(wanted(&pad(7, key::VOL_DOWN)), now, timing(), |_, _| Ok(()))
            .unwrap();
        let mut released = Vec::new();
        state
            .release(|key, down| {
                assert!(!down);
                released.push(key);
                Ok(())
            })
            .unwrap();
        assert_eq!(released, [Key::Up, Key::Left, Key::VolumeDown]);
        state
            .tick(now + Duration::from_secs(20), timing(), |_, _| {
                panic!("released key repeated")
            })
            .unwrap();
        state.release(|_, _| panic!("already released")).unwrap();
    }
    #[test]
    fn rejected_down_is_not_held_and_successful_prior_down_is_cleaned() {
        let mut state = State::default();
        assert!(
            state
                .report(wanted(&pad(1, 0)), Instant::now(), timing(), |key, _| {
                    if key == Key::Right {
                        anyhow::bail!("blocked");
                    }
                    Ok(())
                })
                .is_err()
        );
        assert_eq!(state.held, [true, false, false, false, false, false]);
        let mut released = Vec::new();
        state
            .release(|key, _| {
                released.push(key);
                Ok(())
            })
            .unwrap();
        assert_eq!(released, [Key::Up]);
    }
    #[test]
    fn rejected_release_attempts_all_keys_and_retries_only_remaining_without_repeat() {
        let mut state = State::default();
        let now = Instant::now();
        state
            .report(wanted(&pad(1, key::VOL_DOWN)), now, timing(), |_, _| Ok(()))
            .unwrap();
        let mut attempted = Vec::new();
        assert!(
            state
                .release(|key, down| {
                    assert!(!down);
                    attempted.push(key);
                    if key == Key::Up {
                        anyhow::bail!("blocked");
                    }
                    Ok(())
                })
                .is_err()
        );
        assert_eq!(attempted, [Key::Up, Key::Right, Key::VolumeDown]);
        state
            .tick(now + Duration::from_secs(20), timing(), |_, _| {
                panic!("release failed but must not repeat")
            })
            .unwrap();
        let mut retry = Vec::new();
        state
            .release(|key, _| {
                retry.push(key);
                Ok(())
            })
            .unwrap();
        assert_eq!(retry, [Key::Up]);
    }
    #[test]
    fn failed_direction_release_does_not_press_opposite_key() {
        let mut state = State::default();
        let now = Instant::now();
        state
            .report(wanted(&pad(0, 0)), now, timing(), |_, _| Ok(()))
            .unwrap();
        let mut events = Vec::new();
        assert!(
            state
                .report(wanted(&pad(4, 0)), now, timing(), |key, down| {
                    events.push((key, down));
                    anyhow::bail!("blocked")
                })
                .is_err()
        );
        assert_eq!(events, [(Key::Up, false)]);
        state
            .tick(now + Duration::from_secs(20), timing(), |_, _| {
                panic!("stale direction repeated")
            })
            .unwrap();
    }
    #[test]
    fn windows_repeat_bounds_and_volume_limit() {
        let slow = Timing::from_settings(3, 0);
        let fast = Timing::from_settings(0, 31);
        assert_eq!(slow.delay, Duration::from_secs(1));
        assert_eq!(slow.interval, Duration::from_millis(400));
        assert_eq!(fast.delay, Duration::from_millis(250));
        assert!(fast.interval < Duration::from_millis(34));
        assert_eq!(
            fast.interval_for(Key::VolumeDown),
            Duration::from_millis(100)
        );
    }
    #[test]
    fn native_events_use_extended_arrow_scan_codes_and_no_volume_keystrokes() {
        for (key, scan) in [
            (Key::Up, 0x48),
            (Key::Right, 0x4d),
            (Key::Down, 0x50),
            (Key::Left, 0x4b),
        ] {
            let down = unsafe { input(key, true).unwrap().Anonymous.ki };
            let up = unsafe { input(key, false).unwrap().Anonymous.ki };
            assert_eq!(down.wVk, VIRTUAL_KEY(0));
            assert_eq!(down.wScan, scan);
            assert_eq!(down.dwFlags, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_SCANCODE);
            assert_eq!(
                up.dwFlags,
                KEYEVENTF_EXTENDEDKEY | KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP
            );
        }
        for key in [Key::VolumeUp, Key::VolumeDown] {
            assert!(input(key, true).is_none());
            assert!(input(key, false).is_none());
        }
    }
}
