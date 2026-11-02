//! Canonical user-selected desktop bindings. Pairing credentials are separate.
use anyhow::{Result, ensure};
use serde::{Deserialize, Serialize};
use std::{
    collections::BTreeMap,
    path::{Path, PathBuf},
    time::{Duration, Instant},
};

pub const IDS: [&str; 19] = [
    "a",
    "b",
    "x",
    "y",
    "l1",
    "l2",
    "r1",
    "r2",
    "home",
    "play_pause",
    "back",
    "left_stick",
    "right_stick",
    "dpad_up",
    "dpad_down",
    "dpad_left",
    "dpad_right",
    "volume_down",
    "volume_up",
];

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum Action {
    None,
    Key { key: u16 },
    MouseLeft,
    MouseRight,
    MouseMiddle,
    Cursor,
    VolumeDown,
    VolumeUp,
    MediaPlayPause,
    MediaStop,
    Launch { path: String },
}

// Serde's internally tagged unit variants ignore unused content. Struct
// variants (including the empty ones) enforce the exact action contract.
impl<'de> Deserialize<'de> for Action {
    fn deserialize<D: serde::Deserializer<'de>>(
        deserializer: D,
    ) -> std::result::Result<Self, D::Error> {
        #[derive(Deserialize)]
        #[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
        enum StrictAction {
            None {},
            Key { key: u16 },
            MouseLeft {},
            MouseRight {},
            MouseMiddle {},
            Cursor {},
            VolumeDown {},
            VolumeUp {},
            MediaPlayPause {},
            MediaStop {},
            Launch { path: String },
        }
        Ok(match StrictAction::deserialize(deserializer)? {
            StrictAction::None {} => Self::None,
            StrictAction::Key { key } => Self::Key { key },
            StrictAction::MouseLeft {} => Self::MouseLeft,
            StrictAction::MouseRight {} => Self::MouseRight,
            StrictAction::MouseMiddle {} => Self::MouseMiddle,
            StrictAction::Cursor {} => Self::Cursor,
            StrictAction::VolumeDown {} => Self::VolumeDown,
            StrictAction::VolumeUp {} => Self::VolumeUp,
            StrictAction::MediaPlayPause {} => Self::MediaPlayPause,
            StrictAction::MediaStop {} => Self::MediaStop,
            StrictAction::Launch { path } => Self::Launch { path },
        })
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum StickMode {
    Cursor,
    Scroll,
    Arrows,
    Wasd,
    None,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Curve {
    Precise,
    Linear,
    Quick,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum Smoothing {
    Off,
    Light,
    Steady,
}

#[derive(Clone, Copy, Debug, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Stick {
    pub mode: StickMode,
    pub sensitivity: f64,
    pub curve: Curve,
    pub deadzone: f64,
    pub smoothing: Smoothing,
}
impl Default for Stick {
    fn default() -> Self {
        Self {
            mode: StickMode::Cursor,
            sensitivity: 1.0,
            curve: Curve::Precise,
            deadzone: 0.20,
            smoothing: Smoothing::Off,
        }
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Sticks {
    pub left: Stick,
    pub right: Stick,
}
impl Default for Sticks {
    fn default() -> Self {
        Self {
            left: Stick::default(),
            right: Stick {
                mode: StickMode::Scroll,
                ..Stick::default()
            },
        }
    }
}
impl Sticks {
    pub fn as_array(&self) -> [Stick; 2] {
        [self.left, self.right]
    }
    fn validate(&self) -> Result<()> {
        for stick in self.as_array() {
            ensure!(
                stick.sensitivity.is_finite() && (0.25..=3.0).contains(&stick.sensitivity),
                "controls_stick_sensitivity_range"
            );
            ensure!(
                stick.deadzone.is_finite() && (0.0..=0.35).contains(&stick.deadzone),
                "controls_stick_deadzone_range"
            );
        }
        Ok(())
    }
}

#[derive(Clone, Debug, PartialEq, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct Profile {
    pub schema: u32,
    pub sensitivity: f64,
    // Additive desktop movement settings. Existing button mappings keep their
    // values; absent movement settings get the same defaults as a fresh profile.
    #[serde(default)]
    pub sticks: Sticks,
    #[serde(deserialize_with = "bindings_without_duplicates")]
    pub bindings: BTreeMap<String, Action>,
}
fn bindings_without_duplicates<'de, D: serde::Deserializer<'de>>(
    deserializer: D,
) -> std::result::Result<BTreeMap<String, Action>, D::Error> {
    struct Bindings;
    impl<'de> serde::de::Visitor<'de> for Bindings {
        type Value = BTreeMap<String, Action>;
        fn expecting(&self, f: &mut std::fmt::Formatter) -> std::fmt::Result {
            f.write_str("unique controller bindings")
        }
        fn visit_map<M: serde::de::MapAccess<'de>>(
            self,
            mut map: M,
        ) -> std::result::Result<Self::Value, M::Error> {
            let mut bindings = BTreeMap::new();
            while let Some((id, action)) = map.next_entry::<String, Action>()? {
                if bindings.insert(id, action).is_some() {
                    return Err(serde::de::Error::custom("duplicate controller binding"));
                }
            }
            Ok(bindings)
        }
    }
    deserializer.deserialize_map(Bindings)
}
impl Default for Profile {
    fn default() -> Self {
        let mut bindings: BTreeMap<_, _> = IDS
            .into_iter()
            .map(|id| (id.into(), Action::None))
            .collect();
        for (id, action) in [
            ("a", Action::MouseLeft),
            ("b", Action::MouseRight),
            ("dpad_up", Action::Key { key: 0x26 }),
            ("dpad_down", Action::Key { key: 0x28 }),
            ("dpad_left", Action::Key { key: 0x25 }),
            ("dpad_right", Action::Key { key: 0x27 }),
            ("volume_down", Action::VolumeDown),
            ("volume_up", Action::VolumeUp),
            ("play_pause", Action::MediaPlayPause),
            ("home", Action::Key { key: 0x24 }),
            ("back", Action::Key { key: 0x1b }),
        ] {
            bindings.insert(id.into(), action);
        }
        Self {
            schema: 1,
            sensitivity: 1.0,
            sticks: Sticks::default(),
            bindings,
        }
    }
}
impl Profile {
    pub fn parse(bytes: &[u8]) -> Result<Self> {
        ensure!(bytes.len() <= 65536, "controls_profile_oversized");
        let value: Self = serde_json::from_slice(bytes)?;
        value.validate()?;
        Ok(value)
    }
    pub fn validate(&self) -> Result<()> {
        ensure!(self.schema == 1, "controls_profile_schema");
        ensure!(
            self.sensitivity.is_finite() && (0.25..=3.0).contains(&self.sensitivity),
            "controls_sensitivity_range"
        );
        ensure!(
            self.bindings.len() == IDS.len()
                && IDS.iter().all(|id| self.bindings.contains_key(*id)),
            "controls_bindings_incomplete"
        );
        self.sticks.validate()?;
        for (id, action) in &self.bindings {
            match action {
                Action::Cursor => ensure!(
                    ["dpad_up", "dpad_down", "dpad_left", "dpad_right"].contains(&id.as_str()),
                    "controls_cursor_requires_direction"
                ),
                Action::Key { key } => ensure!((8..=254).contains(key), "controls_key_range"),
                Action::Launch { path } => validate_launch_path(path)?,
                _ => {}
            }
        }
        Ok(())
    }
}

pub fn validate_launch_path(value: &str) -> Result<()> {
    let path = Path::new(value);
    ensure!(
        value.len() <= 32700
            && !value.chars().any(|c| c.is_control() || c == '"')
            && path.is_absolute(),
        "controls_launch_path"
    );
    let extension = path
        .extension()
        .and_then(|x| x.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    ensure!(
        ["exe", "cmd", "bat", "ps1"].contains(&extension.as_str()),
        "controls_launch_type"
    );
    ensure!(
        !["cmd", "bat"].contains(&extension.as_str()) || !value.contains(['%', '!']),
        "controls_batch_expansion_path"
    );
    Ok(())
}

pub struct Watcher {
    path: PathBuf,
    last: Option<Vec<u8>>,
    next_check: Instant,
    pub errors: u64,
}
impl Watcher {
    pub fn installed() -> Result<Self> {
        Ok(Self {
            path: crate::lease::root()?.join("controls.json"),
            last: None,
            next_check: Instant::now(),
            errors: 0,
        })
    }
    pub fn poll(&mut self, now: Instant) -> Option<Profile> {
        self.poll_with(now, |path| match path.metadata() {
            Ok(meta) => {
                ensure!(meta.len() <= 65536, "controls_profile_oversized");
                Ok(Some(crate::lease::bounded_read(path)?))
            }
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(None),
            Err(error) => Err(error.into()),
        })
    }
    fn poll_with(
        &mut self,
        now: Instant,
        read: impl FnOnce(&Path) -> Result<Option<Vec<u8>>>,
    ) -> Option<Profile> {
        if now < self.next_check {
            return None;
        }
        self.next_check = now + Duration::from_millis(200);
        let result = read(&self.path).and_then(|bytes| match bytes {
            Some(bytes) => Ok(bytes),
            None => Ok(serde_json::to_vec(&Profile::default())?),
        });
        match result {
            Ok(bytes) if self.last.as_ref() == Some(&bytes) => None,
            Ok(bytes) => {
                let parsed = Profile::parse(&bytes);
                self.last = Some(bytes);
                match parsed {
                    Ok(profile) => Some(profile),
                    Err(_) => {
                        self.errors += 1;
                        None
                    }
                }
            }
            Err(_) => {
                self.errors += 1;
                None
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn current_defaults_cover_every_control() {
        let p = Profile::default();
        p.validate().unwrap();
        assert_eq!(p.bindings["a"], Action::MouseLeft);
        assert_eq!(p.bindings["left_stick"], Action::None);
        assert_eq!(p.bindings["play_pause"], Action::MediaPlayPause);
        assert_eq!(Profile::parse(&serde_json::to_vec(&p).unwrap()).unwrap(), p);
        assert_eq!(
            Profile::parse(include_bytes!("../../contracts/default-controls.json")).unwrap(),
            p
        );
    }
    #[test]
    fn malformed_profiles_cannot_install_actions() {
        let mut p = Profile::default();
        p.bindings.remove("a");
        assert!(p.validate().is_err());
        p = Profile::default();
        p.sensitivity = 3.01;
        assert!(p.validate().is_err());
        p = Profile::default();
        p.bindings.insert("a".into(), Action::Key { key: 1 });
        assert!(p.validate().is_err());
        assert!(
            serde_json::from_str::<Action>(r#"{"kind":"none","path":"C:\\anything.exe"}"#).is_err()
        );
        for action in [
            r#"{"kind":"none","path":null}"#,
            r#"{"kind":"mouse_left","key":8}"#,
            r#"{"kind":"key","key":8,"key":9}"#,
            r#"{"kind":"none","kind":"launch","path":"C:\\anything.exe"}"#,
            r#"{"kind":"launch","path":null}"#,
        ] {
            assert!(serde_json::from_str::<Action>(action).is_err(), "{action}");
        }
        assert!(Profile::parse(&vec![b' '; 65537]).is_err());
        let text = String::from_utf8(serde_json::to_vec(&Profile::default()).unwrap()).unwrap();
        assert!(
            Profile::parse(
                text.replace("\"a\":", "\"a\":{\"kind\":\"none\"},\"a\":")
                    .as_bytes()
            )
            .is_err()
        );
    }
    #[test]
    fn launch_paths_are_exact_supported_files() {
        for path in [
            r"C:\Tools\My App.exe",
            r"C:\Tools\A & B.cmd",
            r"C:\Tools\task.ps1",
        ] {
            validate_launch_path(path).unwrap();
        }
        for path in [
            r"relative.exe",
            r"C:\Tools\x.txt",
            r"C:\Tools\%PATH%.cmd",
            r"C:\Tools\!evil!.bat",
            "C:\\x.exe\n",
        ] {
            assert!(validate_launch_path(path).is_err(), "{path}");
        }
    }
    #[test]
    fn movement_contract_preserves_existing_buttons_and_rejects_partial_settings() {
        let mut value = serde_json::to_value(Profile::default()).unwrap();
        value["bindings"]["left_stick"] = serde_json::json!({"kind":"key","key":65});
        value.as_object_mut().unwrap().remove("sticks");
        let profile = Profile::parse(&serde_json::to_vec(&value).unwrap()).unwrap();
        assert_eq!(profile.sticks, Sticks::default());
        assert_eq!(profile.bindings["left_stick"], Action::Key { key: 65 });
        for malformed in [
            serde_json::Value::Null,
            serde_json::json!({}),
            serde_json::json!({"left":serde_json::to_value(Stick::default()).unwrap()}),
        ] {
            value["sticks"] = malformed;
            assert!(Profile::parse(&serde_json::to_vec(&value).unwrap()).is_err());
        }
        value["sticks"] = serde_json::to_value(Sticks::default()).unwrap();
        for (field, invalid) in [
            ("mode", serde_json::json!("launch")),
            ("sensitivity", serde_json::json!(4)),
            ("deadzone", serde_json::json!(-0.1)),
            ("curve", serde_json::json!("invented")),
            ("smoothing", serde_json::json!(true)),
        ] {
            let mut broken = value.clone();
            broken["sticks"]["left"][field] = invalid;
            assert!(
                Profile::parse(&serde_json::to_vec(&broken).unwrap()).is_err(),
                "{field}"
            );
            let mut missing = value.clone();
            missing["sticks"]["left"]
                .as_object_mut()
                .unwrap()
                .remove(field);
            assert!(
                Profile::parse(&serde_json::to_vec(&missing).unwrap()).is_err(),
                "missing {field}"
            );
        }
        for number in [f64::NAN, f64::INFINITY] {
            let mut profile = Profile::default();
            profile.sticks.left.deadzone = number;
            assert!(profile.validate().is_err());
            profile.sticks.left.deadzone = 0.2;
            profile.sticks.right.sensitivity = number;
            assert!(profile.validate().is_err());
        }
    }
    #[test]
    fn cursor_actions_require_a_direction() {
        for id in IDS {
            let mut profile = Profile::default();
            profile.bindings.insert(id.into(), Action::Cursor);
            assert_eq!(profile.validate().is_ok(), id.starts_with("dpad_"));
        }
        assert!(serde_json::from_str::<Action>(r#"{"kind":"cursor","key":38}"#).is_err());
    }
    #[test]
    fn reload_keeps_last_good_profile_until_valid_or_deleted() {
        let start = Instant::now();
        let mut watcher = Watcher {
            path: PathBuf::from("unused-test-path"),
            last: None,
            next_check: start,
            errors: 0,
        };
        let mut active = Profile::default();
        let mut custom = active.clone();
        custom.sensitivity = 1.6;
        let valid = serde_json::to_vec(&custom).unwrap();
        active = watcher
            .poll_with(start, |_| Ok(Some(valid.clone())))
            .unwrap();
        assert_eq!(active, custom);
        // Every 200ms sample consumes a complete write. Invalid/empty content
        // returns no replacement, so the caller retains its last good profile.
        assert!(watcher.poll_with(start, |_| panic!("too early")).is_none());
        for (step, invalid) in [(1, b"{".to_vec()), (2, Vec::new())] {
            assert!(
                watcher
                    .poll_with(start + Duration::from_millis(step * 200), |_| Ok(Some(
                        invalid
                    )))
                    .is_none()
            );
            assert_eq!(active, custom);
        }
        assert_eq!(watcher.errors, 2);
        assert!(
            watcher
                .poll_with(start + Duration::from_millis(600), |_| Ok(Some(Vec::new())))
                .is_none()
        );
        assert_eq!(watcher.errors, 2); // Same malformed bytes do not flood diagnostics.
        custom.sensitivity = 2.0;
        active = watcher
            .poll_with(start + Duration::from_millis(800), |_| {
                Ok(Some(serde_json::to_vec(&custom).unwrap()))
            })
            .unwrap();
        assert_eq!(active, custom);
        assert!(
            watcher
                .poll_with(start + Duration::from_millis(1000), |_| Err(
                    anyhow::anyhow!("sharing_violation")
                ))
                .is_none()
        );
        assert_eq!(active, custom);
        active = watcher
            .poll_with(start + Duration::from_millis(1200), |_| Ok(None))
            .unwrap();
        assert_eq!(active, Profile::default());
    }
}
