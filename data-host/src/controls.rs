//! Desktop action state machine. XInput remains the original physical gamepad.
use crate::{
    controller::{Pad, btn, key},
    keyboard::{self, Timing},
    mouse::Mouse,
    profile::{Action, IDS, Profile, Watcher},
};
use anyhow::Result;
use std::{
    collections::{BTreeMap, BTreeSet},
    time::{Duration, Instant},
};

trait Sink {
    fn key(&mut self, key: u16, down: bool) -> Result<()>;
    fn mouse(&mut self, buttons: u8) -> Result<()>;
    fn volume(&mut self, increase: bool);
    fn launch(&mut self, path: &str);
}
fn physical(p: &Pad, previous: &[bool; 19]) -> [bool; 19] {
    let trigger = |value: u16, held: bool| if held { value > 26214 } else { value >= 32768 };
    [
        p.buttons & btn::A != 0,
        p.buttons & btn::B != 0,
        p.buttons & btn::X != 0,
        p.buttons & btn::Y != 0,
        p.buttons & btn::LB != 0,
        trigger(p.lt, previous[5]),
        p.buttons & btn::RB != 0,
        trigger(p.rt, previous[7]),
        p.keys & key::HOME != 0,
        p.buttons & btn::START != 0,
        p.keys & key::BACK != 0,
        p.buttons & btn::L3 != 0,
        p.buttons & btn::R3 != 0,
        matches!(p.hat, 0 | 1 | 7),
        matches!(p.hat, 3 | 4 | 5),
        matches!(p.hat, 5 | 6 | 7),
        matches!(p.hat, 1 | 2 | 3),
        p.keys & key::VOL_DOWN != 0,
        p.keys & key::VOL_UP != 0,
    ]
}
fn canonical_key(key: u16) -> u16 {
    match key {
        0x10 => 0xa0,
        0x11 => 0xa2,
        0x12 => 0xa4,
        _ => key,
    }
}

struct Engine {
    profile: Profile,
    physical: [bool; 19],
    blocked: [bool; 19],
    held: BTreeMap<u16, Option<Instant>>,
    volume: [Option<Instant>; 2],
}
impl Default for Engine {
    fn default() -> Self {
        Self {
            profile: Profile::default(),
            physical: [false; 19],
            blocked: [false; 19],
            held: BTreeMap::new(),
            volume: [None; 2],
        }
    }
}
impl Engine {
    fn profile(&mut self, profile: Profile, sink: &mut impl Sink) -> Result<()> {
        if profile.bindings != self.profile.bindings {
            let physical = self.physical;
            self.release(sink)?;
            self.physical = physical;
            self.blocked = physical;
        }
        self.profile = profile;
        Ok(())
    }
    fn report(
        &mut self,
        p: &Pad,
        now: Instant,
        timing: Timing,
        sink: &mut impl Sink,
    ) -> Result<()> {
        let before: Vec<bool> = self
            .physical
            .iter()
            .zip(self.blocked)
            .map(|(down, blocked)| *down && !blocked)
            .collect();
        self.physical = physical(p, &self.physical);
        for i in 0..19 {
            self.blocked[i] &= self.physical[i];
        }
        let mut desired = BTreeSet::new();
        let mut mouse = 0;
        let mut volume = [false; 2];
        let mut launch = Vec::new();
        for (i, id) in IDS.iter().enumerate() {
            if !self.physical[i] || self.blocked[i] {
                continue;
            }
            match &self.profile.bindings[*id] {
                Action::None => {}
                Action::Key { key } => {
                    desired.insert(canonical_key(*key));
                }
                Action::MediaPlayPause => {
                    desired.insert(0xb3);
                }
                Action::MediaStop => {
                    desired.insert(0xb2);
                }
                Action::MouseLeft => mouse |= 1,
                Action::MouseRight => mouse |= 2,
                Action::MouseMiddle => mouse |= 4,
                Action::VolumeDown => volume[0] = true,
                Action::VolumeUp => volume[1] = true,
                Action::Launch { path } if !before[i] => launch.push(path.clone()),
                Action::Launch { .. } => {}
            }
        }
        // All releases precede new presses; duplicate bindings share ownership.
        for key in self.held.keys().copied().collect::<Vec<_>>() {
            if !desired.contains(&key) {
                sink.key(key, false)?;
                self.held.remove(&key);
            }
        }
        let mut presses: Vec<_> = desired.into_iter().collect();
        // A modifier and letter may arrive together in one controller report.
        // Establish modifiers first so that the first letter is a chord too.
        presses.sort_by_key(|key| (!matches!(key, 0x5b | 0x5c | 0xa0..=0xa5), *key));
        for key in presses {
            if !self.held.contains_key(&key) {
                sink.key(key, true)?;
                self.held
                    .insert(key, keyboard::repeats(key).then_some(now + timing.delay));
            }
        }
        sink.mouse(mouse)?;
        for i in 0..2 {
            if volume[i] {
                if self.volume[i].is_none() {
                    sink.volume(i == 1);
                    self.volume[i] = Some(now + timing.delay);
                }
            } else {
                self.volume[i] = None;
            }
        }
        for path in launch {
            sink.launch(&path);
        }
        Ok(())
    }
    fn tick(&mut self, now: Instant, timing: Timing, sink: &mut impl Sink) -> Result<()> {
        for (key, due) in &mut self.held {
            if due.is_some_and(|at| now >= at) {
                sink.key(*key, true)?;
                *due = Some(now + timing.interval);
            }
        }
        for i in 0..2 {
            if self.volume[i].is_some_and(|at| now >= at) {
                sink.volume(i == 1);
                self.volume[i] = Some(now + timing.interval.max(Duration::from_millis(100)));
            }
        }
        Ok(())
    }
    fn release(&mut self, sink: &mut impl Sink) -> Result<()> {
        self.physical = [false; 19];
        self.blocked = [false; 19];
        self.volume = [None; 2];
        let mut result = Ok(());
        for key in self.held.keys().copied().collect::<Vec<_>>() {
            self.held.insert(key, None);
            match sink.key(key, false) {
                Ok(()) => {
                    self.held.remove(&key);
                }
                Err(error) => {
                    if result.is_ok() {
                        result = Err(error)
                    }
                }
            }
        }
        let mouse = sink.mouse(0);
        result.and(mouse)
    }
}

struct Native<'a> {
    mouse: &'a mut Mouse,
    launch_errors: &'a mut u64,
    launches: &'a mut u64,
}
impl Sink for Native<'_> {
    fn key(&mut self, key: u16, down: bool) -> Result<()> {
        keyboard::emit(key, down)
    }
    fn mouse(&mut self, buttons: u8) -> Result<()> {
        self.mouse.mapped_buttons(buttons)
    }
    fn volume(&mut self, increase: bool) {
        crate::volume::adjust(increase);
    }
    fn launch(&mut self, path: &str) {
        if crate::launch::run(path).is_ok() {
            *self.launches += 1;
        } else {
            *self.launch_errors += 1;
        }
    }
}

pub struct Controls {
    engine: Engine,
    watcher: Option<Watcher>,
    timing: Timing,
    pub reloads: u64,
    pub launch_errors: u64,
    pub launches: u64,
}
impl Default for Controls {
    fn default() -> Self {
        Self {
            engine: Engine::default(),
            watcher: None,
            timing: Timing::default(),
            reloads: 0,
            launch_errors: 0,
            launches: 0,
        }
    }
}
impl Controls {
    pub fn initialize(&mut self, mouse: &mut Mouse) -> Result<()> {
        self.timing = Timing::read()?;
        self.watcher = Some(Watcher::installed()?);
        self.tick(Instant::now(), mouse)
    }
    pub fn profile_errors(&self) -> u64 {
        self.watcher.as_ref().map_or(0, |w| w.errors)
    }
    pub fn report(&mut self, p: &Pad, now: Instant, mouse: &mut Mouse) -> Result<()> {
        self.engine.report(
            p,
            now,
            self.timing,
            &mut Native {
                mouse,
                launch_errors: &mut self.launch_errors,
                launches: &mut self.launches,
            },
        )
    }
    pub fn tick(&mut self, now: Instant, mouse: &mut Mouse) -> Result<()> {
        if let Some(profile) = self.watcher.as_mut().and_then(|w| w.poll(now)) {
            if profile != self.engine.profile {
                self.engine.profile(
                    profile,
                    &mut Native {
                        mouse,
                        launch_errors: &mut self.launch_errors,
                        launches: &mut self.launches,
                    },
                )?;
                self.reloads += 1;
            }
        }
        mouse.sensitivity(self.engine.profile.sensitivity);
        self.engine.tick(
            now,
            self.timing,
            &mut Native {
                mouse,
                launch_errors: &mut self.launch_errors,
                launches: &mut self.launches,
            },
        )
    }
    pub fn release(&mut self, mouse: &mut Mouse) -> Result<()> {
        self.engine.release(&mut Native {
            mouse,
            launch_errors: &mut self.launch_errors,
            launches: &mut self.launches,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use anyhow::ensure;
    #[derive(Default)]
    struct Fake {
        keys: Vec<(u16, bool)>,
        mouse: u8,
        volumes: Vec<bool>,
        launches: Vec<String>,
        reject: Option<u16>,
    }
    impl Sink for Fake {
        fn key(&mut self, k: u16, d: bool) -> Result<()> {
            self.keys.push((k, d));
            ensure!(self.reject != Some(k), "rejected");
            Ok(())
        }
        fn mouse(&mut self, b: u8) -> Result<()> {
            self.mouse = b;
            Ok(())
        }
        fn volume(&mut self, up: bool) {
            self.volumes.push(up)
        }
        fn launch(&mut self, path: &str) {
            self.launches.push(path.into())
        }
    }
    fn p(buttons: u16) -> Pad {
        Pad {
            buttons,
            ..Pad::default()
        }
    }
    #[test]
    fn default_controls_and_trigger_hysteresis() {
        let mut e = Engine::default();
        let mut s = Fake::default();
        let now = Instant::now();
        e.report(
            &Pad {
                buttons: btn::A | btn::B | btn::START,
                hat: 1,
                keys: key::VOL_DOWN,
                ..Pad::default()
            },
            now,
            Timing::default(),
            &mut s,
        )
        .unwrap();
        assert_eq!(s.mouse, 3);
        assert_eq!(s.keys, [(0x26, true), (0x27, true), (0xb3, true)]);
        assert_eq!(s.volumes, [false]);
        let mut previous = [false; 19];
        assert!(
            !physical(
                &Pad {
                    lt: 32000,
                    ..Pad::default()
                },
                &previous
            )[5]
        );
        previous[5] = true;
        assert!(
            physical(
                &Pad {
                    lt: 32000,
                    ..Pad::default()
                },
                &previous
            )[5]
        );
        assert!(
            !physical(
                &Pad {
                    lt: 26000,
                    ..Pad::default()
                },
                &previous
            )[5]
        );
    }
    #[test]
    fn duplicate_keys_and_modifier_aliases_keep_ownership() {
        let mut e = Engine::default();
        e.profile
            .bindings
            .insert("a".into(), Action::Key { key: 0x11 });
        e.profile
            .bindings
            .insert("b".into(), Action::Key { key: 0xa2 });
        let mut s = Fake::default();
        let now = Instant::now();
        for buttons in [btn::A, btn::A | btn::B, btn::B, 0] {
            e.report(&p(buttons), now, Timing::default(), &mut s)
                .unwrap();
        }
        assert_eq!(s.keys, [(0xa2, true), (0xa2, false)]);
    }
    #[test]
    fn simultaneous_modifier_and_letter_press_is_a_chord() {
        let mut e = Engine::default();
        e.profile
            .bindings
            .insert("a".into(), Action::Key { key: 0x41 });
        e.profile
            .bindings
            .insert("b".into(), Action::Key { key: 0x11 });
        let mut s = Fake::default();
        let now = Instant::now();
        e.report(&p(btn::A | btn::B), now, Timing::default(), &mut s)
            .unwrap();
        e.report(&p(0), now, Timing::default(), &mut s).unwrap();
        assert_eq!(
            s.keys,
            [(0xa2, true), (0x41, true), (0x41, false), (0xa2, false)]
        );
    }
    #[test]
    fn launch_is_one_per_press_and_remap_suppresses_held_sources() {
        let mut e = Engine::default();
        let mut s = Fake::default();
        let now = Instant::now();
        e.report(&p(btn::A), now, Timing::default(), &mut s)
            .unwrap();
        assert_eq!(s.mouse, 1);
        let mut profile = Profile::default();
        profile.bindings.insert(
            "a".into(),
            Action::Launch {
                path: r"C:\example.exe".into(),
            },
        );
        e.profile(profile, &mut s).unwrap();
        assert_eq!(s.mouse, 0);
        e.report(&p(btn::A), now, Timing::default(), &mut s)
            .unwrap();
        assert!(s.launches.is_empty());
        for buttons in [0, btn::A, btn::A] {
            e.report(&p(buttons), now, Timing::default(), &mut s)
                .unwrap();
        }
        e.tick(now + Duration::from_secs(20), Timing::default(), &mut s)
            .unwrap();
        assert_eq!(s.launches.len(), 1);
    }
    #[test]
    fn sensitivity_changes_do_not_break_a_drag() {
        let mut e = Engine::default();
        let mut s = Fake::default();
        let now = Instant::now();
        e.report(&p(btn::A), now, Timing::default(), &mut s)
            .unwrap();
        let mut profile = Profile::default();
        profile.sensitivity = 2.0;
        e.profile(profile, &mut s).unwrap();
        assert_eq!(s.mouse, 1);
        assert!(!e.blocked[0]);
    }
    #[test]
    fn repeats_use_timer_and_release_cancels_all() {
        let mut e = Engine::default();
        let mut s = Fake::default();
        let now = Instant::now();
        let timing = Timing {
            delay: Duration::from_millis(500),
            interval: Duration::from_millis(40),
        };
        e.report(
            &Pad {
                hat: 0,
                keys: key::VOL_UP,
                ..Pad::default()
            },
            now,
            timing,
            &mut s,
        )
        .unwrap();
        e.tick(now + Duration::from_millis(499), timing, &mut s)
            .unwrap();
        assert_eq!(s.keys.len(), 1);
        e.tick(now + Duration::from_millis(500), timing, &mut s)
            .unwrap();
        assert_eq!(s.keys.len(), 2);
        assert_eq!(s.volumes.len(), 2);
        e.tick(now + Duration::from_millis(540), timing, &mut s)
            .unwrap();
        assert_eq!(s.keys.len(), 3);
        assert_eq!(s.volumes.len(), 2);
        e.release(&mut s).unwrap();
        let count = s.keys.len();
        e.tick(now + Duration::from_secs(20), timing, &mut s)
            .unwrap();
        assert_eq!(s.keys.len(), count);
    }
    #[test]
    fn failed_release_retries_without_repeating() {
        let mut e = Engine::default();
        let mut s = Fake::default();
        let now = Instant::now();
        e.report(
            &Pad {
                hat: 1,
                ..Pad::default()
            },
            now,
            Timing::default(),
            &mut s,
        )
        .unwrap();
        s.reject = Some(0x26);
        assert!(e.release(&mut s).is_err());
        assert_eq!(e.held.len(), 1);
        let count = s.keys.len();
        e.tick(now + Duration::from_secs(20), Timing::default(), &mut s)
            .unwrap();
        assert_eq!(s.keys.len(), count);
        s.reject = None;
        e.release(&mut s).unwrap();
        assert!(e.held.is_empty());
    }
}
