//! Rate-based desktop motion. Reports update intent; the clock moves the cursor.
//! Quiet, unchanged HID reports do not expire a held stick. The session owner
//! calls reset on authoritative transport loss, disconnect and shutdown.
use crate::{
    controller::Pad,
    profile::{Curve, Smoothing, Stick, StickMode, Sticks},
};
use std::{
    collections::BTreeSet,
    time::{Duration, Instant},
};

#[derive(Clone, Copy, Debug, Default, PartialEq)]
struct Vector {
    x: f64,
    y: f64,
}
impl Vector {
    fn length(self) -> f64 {
        self.x.hypot(self.y)
    }
    fn scale(self, gain: f64) -> Self {
        Self {
            x: self.x * gain,
            y: self.y * gain,
        }
    }
    fn plus(self, other: Self) -> Self {
        Self {
            x: self.x + other.x,
            y: self.y + other.y,
        }
    }
    fn cap(self, maximum: f64) -> Self {
        let length = self.length();
        if length > maximum && length > 0.0 {
            self.scale(maximum / length)
        } else {
            self
        }
    }
}
fn axis(value: u16) -> f64 {
    // HID declares the centre as 0x7fff. Use each half's full range so both
    // physical extremes have exactly the same maximum speed.
    let value = f64::from(value) - 32767.0;
    value / if value < 0.0 { 32767.0 } else { 32768.0 }
}
fn radial(raw: Vector, deadzone: f64) -> Vector {
    let length = raw.length();
    if length <= deadzone || length == 0.0 {
        return Vector::default();
    }
    raw.scale(((length.min(1.0) - deadzone) / (1.0 - deadzone)) / length)
}
fn shaped(value: Vector, curve: Curve) -> Vector {
    let length = value.length();
    if length == 0.0 {
        return value;
    }
    let output = match curve {
        Curve::Precise => length * length,
        Curve::Linear => length,
        Curve::Quick => length.sqrt(),
    };
    value.scale(output / length)
}

#[derive(Clone, Default)]
pub struct Movement {
    raw: [Vector; 2],
    blocked: [bool; 2],
    directions: [[bool; 4]; 2], // Up, down, left, right; keyboard hysteresis.
    filtered: [Vector; 2],
    dpad: Vector,
    previous_tick: Option<Instant>,
    remainder: Vector,
    wheel_remainder: f64,
    wheel: i32,
}
impl Movement {
    pub fn reconfigure(&mut self, previous: Sticks, current: Sticks) {
        for (i, (old, new)) in previous
            .as_array()
            .into_iter()
            .zip(current.as_array())
            .enumerate()
        {
            if old.mode != new.mode {
                self.blocked[i] = self.raw[i].length() > new.deadzone;
                self.directions[i] = [false; 4];
                self.filtered[i] = Vector::default();
                self.remainder = Vector::default();
                self.wheel_remainder = 0.0;
                self.wheel = 0;
            }
        }
    }
    pub fn report(&mut self, pad: &Pad, settings: Sticks) {
        self.raw = [
            Vector {
                x: axis(pad.lx),
                y: axis(pad.ly),
            },
            Vector {
                x: axis(pad.rx),
                y: axis(pad.ry),
            },
        ];
        for (i, config) in settings.as_array().into_iter().enumerate() {
            if self.raw[i].length() <= config.deadzone {
                self.blocked[i] = false;
            }
            let value = self.input(i, config);
            if matches!(
                config.mode,
                StickMode::Cursor | StickMode::Scroll | StickMode::None
            ) {
                self.directions[i] = [false; 4];
                continue;
            }
            // Digital movement needs deliberate deflection; separate press and
            // release thresholds prevent chatter near the activation boundary.
            for (direction, amount) in [-value.y, value.y, -value.x, value.x]
                .into_iter()
                .enumerate()
            {
                self.directions[i][direction] = amount
                    >= if self.directions[i][direction] {
                        0.35
                    } else {
                        0.45
                    };
            }
        }
    }
    fn input(&self, index: usize, config: Stick) -> Vector {
        if self.blocked[index] {
            Vector::default()
        } else {
            let raw = if config.mode == StickMode::Scroll {
                Vector {
                    x: 0.0,
                    y: self.raw[index].y,
                }
            } else {
                self.raw[index]
            };
            radial(raw, config.deadzone)
        }
    }
    pub fn keys(&self, settings: Sticks) -> BTreeSet<u16> {
        let mut result = BTreeSet::new();
        for (i, config) in settings.as_array().into_iter().enumerate() {
            let keys = match config.mode {
                StickMode::Arrows => [0x26, 0x28, 0x25, 0x27],
                StickMode::Wasd => [0x57, 0x53, 0x41, 0x44],
                _ => continue,
            };
            for (down, key) in self.directions[i].into_iter().zip(keys) {
                if down {
                    result.insert(key);
                }
            }
        }
        result
    }
    pub fn dpad(&mut self, x: f64, y: f64) {
        self.dpad = Vector { x, y }.cap(1.0);
    }
    pub fn active(&self, settings: Sticks) -> bool {
        self.dpad.length() > 0.0
            || settings
                .as_array()
                .into_iter()
                .enumerate()
                .any(|(i, c)| c.mode == StickMode::Cursor && self.input(i, c).length() > 0.0)
    }
    pub fn tick(&mut self, now: Instant, settings: Sticks, pixels_per_second: f64) -> (i32, i32) {
        let seconds = self.previous_tick.replace(now).map_or(0.0, |old| {
            now.saturating_duration_since(old)
                .min(Duration::from_millis(30))
                .as_secs_f64()
        });
        let mut sum = self.dpad;
        let mut scroll = 0.0;
        let mut scroll_ceiling: f64 = 0.0;
        let mut ceiling: f64 = if self.dpad.length() > 0.0 { 1.0 } else { 0.0 };
        for (i, config) in settings.as_array().into_iter().enumerate() {
            let input = self.input(i, config);
            let target = if matches!(config.mode, StickMode::Cursor | StickMode::Scroll) {
                shaped(input, config.curve).scale(config.sensitivity)
            } else {
                Vector::default()
            };
            if target.length() == 0.0 {
                // No coast on release, even with smoothing enabled.
                self.filtered[i] = Vector::default();
            } else {
                ceiling = ceiling.max(config.sensitivity);
                let tau = match config.smoothing {
                    Smoothing::Off => 0.0,
                    Smoothing::Light => 0.025,
                    Smoothing::Steady => 0.060,
                };
                let blend = if tau == 0.0 {
                    1.0
                } else {
                    1.0 - (-seconds / tau).exp()
                };
                self.filtered[i] = self.filtered[i]
                    .scale(1.0 - blend)
                    .plus(target.scale(blend));
            }
            if config.mode == StickMode::Scroll {
                scroll -= self.filtered[i].y;
                scroll_ceiling = scroll_ceiling.max(config.sensitivity);
            } else {
                sum = sum.plus(self.filtered[i]);
            }
        }
        // Standard 120-unit wheel detents; keep small tilts useful in apps
        // that do not accumulate partial wheel messages. Eight detents/sec at full tilt.
        if scroll.abs() < 1e-12 {
            self.wheel_remainder = 0.0;
        } else {
            self.wheel_remainder += scroll.clamp(-scroll_ceiling, scroll_ceiling) * 960.0 * seconds;
        }
        self.wheel = (self.wheel_remainder / 120.0).trunc() as i32 * 120;
        self.wheel_remainder -= f64::from(self.wheel);
        let velocity = sum.cap(ceiling);
        if velocity.length() < 1e-12 {
            self.remainder = Vector::default();
            return (0, 0);
        }
        let travel = velocity
            .scale(pixels_per_second * seconds)
            .plus(self.remainder);
        self.remainder = Vector {
            x: travel.x.fract(),
            y: travel.y.fract(),
        };
        (travel.x.trunc() as i32, travel.y.trunc() as i32)
    }
    pub fn take_wheel(&mut self) -> i32 {
        std::mem::take(&mut self.wheel)
    }
    pub fn reset(&mut self) {
        *self = Self::default();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn pad(x: u16, y: u16) -> Pad {
        Pad {
            lx: x,
            ly: y,
            ..Pad::default()
        }
    }
    fn cursor(config: Stick) -> Sticks {
        Sticks {
            left: config,
            right: Stick {
                mode: StickMode::None,
                ..Stick::default()
            },
        }
    }
    fn travel(step_ms: u64, reports_every: u64) -> i32 {
        let mut movement = Movement::default();
        let settings = cursor(Stick::default());
        let start = Instant::now();
        movement.report(&pad(65535, 32767), settings);
        movement.tick(start, settings, 1000.0);
        (1..=1000 / step_ms)
            .map(|i| {
                if i % reports_every == 0 {
                    movement.report(&pad(65535, 32767), settings);
                }
                movement
                    .tick(start + Duration::from_millis(i * step_ms), settings, 1000.0)
                    .0
            })
            .sum()
    }
    #[test]
    fn centered_and_small_jitter_do_not_move() {
        let settings = Sticks::default();
        let mut m = Movement::default();
        let start = Instant::now();
        for (i, p) in [Pad::default(), pad(35000, 30000), pad(30000, 35000)]
            .into_iter()
            .enumerate()
        {
            m.report(&p, settings);
            assert!(!m.active(settings));
            assert_eq!(
                m.tick(
                    start + Duration::from_millis(i as u64 * 20),
                    settings,
                    1000.0
                ),
                (0, 0)
            );
        }
    }
    #[test]
    fn radial_deadzone_is_rescaled_and_curves_preserve_endpoints() {
        assert_eq!(radial(Vector { x: 0.20, y: 0.0 }, 0.20), Vector::default());
        let v = radial(Vector { x: 0.60, y: 0.0 }, 0.20);
        assert!((v.x - 0.5).abs() < 1e-9);
        assert!((shaped(v, Curve::Precise).x - 0.25).abs() < 1e-9);
        assert!((shaped(v, Curve::Linear).x - 0.5).abs() < 1e-9);
        assert!((shaped(v, Curve::Quick).x - 0.5f64.sqrt()).abs() < 1e-9);
        for curve in [Curve::Precise, Curve::Linear, Curve::Quick] {
            assert_eq!(shaped(Vector::default(), curve), Vector::default());
            assert_eq!(
                shaped(Vector { x: 1.0, y: 0.0 }, curve),
                Vector { x: 1.0, y: 0.0 }
            );
        }
        assert!((radial(Vector { x: 1.0, y: 1.0 }, 0.2).length() - 1.0).abs() < 1e-9);
    }
    #[test]
    fn travel_uses_elapsed_time_not_ticks_or_report_frequency() {
        for step in [2, 5, 10, 20] {
            for report in [1, 5, 10000] {
                assert!((travel(step, report) - 1000).abs() <= 1);
            }
        }
    }
    #[test]
    fn quiet_held_stick_continues_for_seconds_then_neutral_stops() {
        let mut m = Movement::default();
        let settings = cursor(Stick::default());
        let start = Instant::now();
        m.report(&pad(65535, 32767), settings);
        m.tick(start, settings, 1000.0);
        let travel: i32 = (1..=600)
            .map(|i| {
                m.tick(start + Duration::from_millis(i * 10), settings, 1000.0)
                    .0
            })
            .sum();
        assert_eq!(travel, 6000);
        m.report(&Pad::default(), settings);
        assert_eq!(
            m.tick(start + Duration::from_millis(6010), settings, 1000.0),
            (0, 0)
        );
    }
    #[test]
    fn diagonal_multiple_sources_and_opposites_are_bounded() {
        let mut m = Movement::default();
        let settings = Sticks {
            left: Stick::default(),
            right: Stick::default(),
        };
        let start = Instant::now();
        m.report(
            &Pad {
                lx: 65535,
                ly: 65535,
                rx: 65535,
                ry: 65535,
                ..Pad::default()
            },
            settings,
        );
        m.dpad(1.0, 1.0);
        m.tick(start, settings, 1000.0);
        let (x, y) = m.tick(start + Duration::from_millis(20), settings, 1000.0);
        assert!(f64::from(x).hypot(f64::from(y)) <= 20.0);
        assert_eq!(x, y);
        m.report(
            &Pad {
                lx: 0,
                rx: 65535,
                ..Pad::default()
            },
            settings,
        );
        m.dpad(0.0, 0.0);
        assert_eq!(
            m.tick(start + Duration::from_millis(40), settings, 1000.0),
            (0, 0)
        );
    }
    #[test]
    fn slower_ticks_cannot_produce_a_backlog_jump_and_reset_has_no_tail() {
        let mut m = Movement::default();
        let settings = Sticks::default();
        let start = Instant::now();
        m.report(&pad(65535, 32767), settings);
        m.tick(start, settings, 1000.0);
        assert_eq!(
            m.tick(start + Duration::from_secs(10), settings, 1000.0),
            (30, 0)
        );
        m.reset();
        assert_eq!(
            m.tick(start + Duration::from_secs(11), settings, 1000.0),
            (0, 0)
        );
    }
    #[test]
    fn smoothing_is_optional_time_based_and_stops_without_coasting() {
        let start = Instant::now();
        for smoothing in [Smoothing::Off, Smoothing::Light, Smoothing::Steady] {
            let mut m = Movement::default();
            let settings = cursor(Stick {
                smoothing,
                ..Stick::default()
            });
            m.report(&pad(65535, 32767), settings);
            m.tick(start, settings, 1000.0);
            let first = m
                .tick(start + Duration::from_millis(10), settings, 1000.0)
                .0;
            if smoothing == Smoothing::Off {
                assert_eq!(first, 10)
            } else {
                assert!(first < 10)
            }
            m.report(&Pad::default(), settings);
            assert_eq!(
                m.tick(start + Duration::from_millis(20), settings, 1000.0),
                (0, 0)
            );
        }
    }
    #[test]
    fn remapping_a_held_stick_requires_recentering_but_speed_is_live() {
        let mut m = Movement::default();
        let old = Sticks::default();
        let mut current = old;
        current.left.mode = StickMode::Wasd;
        m.report(&pad(65535, 32767), old);
        m.reconfigure(old, current);
        m.report(&pad(65535, 32767), current);
        assert!(m.keys(current).is_empty());
        assert!(!m.active(current));
        m.report(&Pad::default(), current);
        m.report(&pad(65535, 32767), current);
        assert_eq!(m.keys(current), BTreeSet::from([0x44]));
        current.left.mode = StickMode::Cursor;
        m.reconfigure(
            Sticks {
                left: Stick {
                    mode: StickMode::Wasd,
                    ..Stick::default()
                },
                right: Stick::default(),
            },
            current,
        );
        m.report(&Pad::default(), current);
        m.report(&pad(65535, 32767), current);
        let start = Instant::now();
        m.tick(start, current, 1000.0);
        let mut fast = current;
        fast.left.sensitivity = 2.0;
        m.reconfigure(current, fast);
        assert_eq!(
            m.tick(start + Duration::from_millis(10), fast, 1000.0),
            (20, 0)
        );
    }
    #[test]
    fn directional_keys_have_hysteresis_and_do_not_depend_on_cursor_curve() {
        let mut m = Movement::default();
        let settings = cursor(Stick {
            mode: StickMode::Arrows,
            curve: Curve::Quick,
            ..Stick::default()
        });
        for (x, held) in [(48000, false), (54000, true), (50000, true), (47000, false)] {
            m.report(&pad(x, 32767), settings);
            assert_eq!(m.keys(settings).contains(&0x27), held);
        }
    }

    #[test]
    fn default_right_stick_scrolls_only_vertically_at_elapsed_time_rate() {
        for step in [2, 5, 10, 20] {
            for (y, sign) in [(0, 1), (65535, -1)] {
                let mut m = Movement::default();
                let settings = Sticks::default();
                let start = Instant::now();
                m.report(
                    &Pad {
                        rx: 65535,
                        ry: y,
                        ..Pad::default()
                    },
                    settings,
                );
                m.tick(start, settings, 1000.0);
                let mut total = 0;
                for i in 1..=1000 / step {
                    assert_eq!(
                        m.tick(start + Duration::from_millis(i * step), settings, 1000.0),
                        (0, 0)
                    );
                    let wheel = m.take_wheel();
                    assert_eq!(wheel % 120, 0);
                    total += wheel;
                }
                assert!((total - sign * 960).abs() <= 120);
                m.report(&Pad::default(), settings);
                m.tick(start + Duration::from_millis(1010), settings, 1000.0);
                assert_eq!(m.take_wheel(), 0);
                m.report(
                    &Pad {
                        rx: 65535,
                        ..Pad::default()
                    },
                    settings,
                );
                m.tick(start + Duration::from_millis(1030), settings, 1000.0);
                assert_eq!(m.take_wheel(), 0);
            }
        }
    }

    #[test]
    fn scroll_reset_and_mode_change_clear_partial_detents() {
        let mut m = Movement::default();
        let settings = Sticks::default();
        let start = Instant::now();
        m.report(
            &Pad {
                ry: 0,
                ..Pad::default()
            },
            settings,
        );
        m.tick(start, settings, 0.0);
        m.tick(start + Duration::from_millis(100), settings, 0.0);
        assert_eq!(m.take_wheel(), 0); // Delayed tick is bounded to 30 ms.
        m.reset();
        m.tick(start + Duration::from_secs(1), settings, 0.0);
        assert_eq!(m.take_wheel(), 0);
        let mut current = settings;
        current.right.mode = StickMode::Cursor;
        m.report(
            &Pad {
                ry: 0,
                ..Pad::default()
            },
            settings,
        );
        m.reconfigure(settings, current);
        m.tick(start + Duration::from_millis(1020), current, 1000.0);
        assert_eq!(m.take_wheel(), 0);
        assert!(!m.active(current));
    }
}
