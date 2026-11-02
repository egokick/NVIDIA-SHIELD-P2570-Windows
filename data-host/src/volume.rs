//! System playback volume. Resolve the default multimedia endpoint for each step
//! so changing headphones/speakers never leaves the controller on an old device.
use anyhow::{Result, ensure};
use std::{
    marker::PhantomData,
    rc::Rc,
    sync::atomic::{AtomicU32, AtomicU64, Ordering},
};
use windows::Win32::{
    Devices::FunctionDiscovery::PKEY_Device_FriendlyName,
    Media::Audio::{
        Endpoints::IAudioEndpointVolume, IMMDevice, IMMDeviceEnumerator, MMDeviceEnumerator,
        eMultimedia, eRender,
    },
    System::Com::{
        CLSCTX_ALL, COINIT_MULTITHREADED, CoCreateInstance, CoInitializeEx, CoTaskMemFree,
        CoUninitialize, STGM_READ,
        StructuredStorage::{PropVariantClear, PropVariantToStringAlloc},
    },
};

// Own exactly one successful COM initialization on this thread, including S_FALSE.
// !Send/!Sync guarantees that CoUninitialize runs on the same thread.
struct Apartment(PhantomData<Rc<()>>);
impl Apartment {
    fn enter() -> Result<Self> {
        unsafe {
            CoInitializeEx(None, COINIT_MULTITHREADED).ok()?;
        }
        Ok(Self(PhantomData))
    }
}
impl Drop for Apartment {
    fn drop(&mut self) {
        unsafe {
            CoUninitialize();
        }
    }
}

static STEPS: AtomicU64 = AtomicU64::new(0);
static ERRORS: AtomicU64 = AtomicU64::new(0);
static LEVEL: AtomicU32 = AtomicU32::new(101);
static BACKEND: AtomicU32 = AtomicU32::new(0);

pub fn backend() -> &'static str {
    match BACKEND.load(Ordering::Relaxed) {
        1 => "windows-master",
        2 => "arctis-headset",
        _ => "none",
    }
}

fn friendly(device: &IMMDevice) -> Result<String> {
    unsafe {
        let store = device.OpenPropertyStore(STGM_READ)?;
        let mut value = store.GetValue(&PKEY_Device_FriendlyName)?;
        let text = PropVariantToStringAlloc(&value);
        let _ = PropVariantClear(&mut value);
        let text = text?;
        let name = String::from_utf16_lossy(text.as_wide());
        CoTaskMemFree(Some(text.0.cast()));
        Ok(name)
    }
}

fn fixed_arctis(name: &str, min: f32, max: f32) -> bool {
    name.eq_ignore_ascii_case("Headphones (Arctis Nova Pro Wireless)") && min == 0.0 && max == 0.0
}

pub fn counters() -> (u64, u64, Option<u32>) {
    let level = LEVEL.load(Ordering::Relaxed);
    (
        STEPS.load(Ordering::Relaxed),
        ERRORS.load(Ordering::Relaxed),
        (level <= 100).then_some(level),
    )
}

pub fn adjust(increase: bool) {
    // Audio endpoint availability must not interrupt the wireless gamepad or
    // leave desktop keys held. Surface failures in the session diagnostics.
    match step(increase) {
        Ok(level) => {
            STEPS.fetch_add(1, Ordering::Relaxed);
            LEVEL.store(level, Ordering::Relaxed);
        }
        Err(_) => {
            ERRORS.fetch_add(1, Ordering::Relaxed);
        }
    }
}

fn step(increase: bool) -> Result<u32> {
    // Declared first, so all endpoint interfaces are released before uninitializing.
    let _apartment = Apartment::enter()?;
    unsafe {
        let enumerator: IMMDeviceEnumerator =
            CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL)?;
        let device = enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia)?;
        let volume: IAudioEndpointVolume = device.Activate(CLSCTX_ALL, None)?;
        let (mut min, mut max, mut increment) = (0.0, 0.0, 0.0);
        volume.GetVolumeRange(&mut min, &mut max, &mut increment)?;
        if fixed_arctis(&friendly(&device)?, min, max) {
            // This endpoint reports a fixed 0 dB range, so Windows master-volume
            // writes cannot attenuate it. Its documented HID control adjusts the
            // headphones themselves; do not change any driver or audio routing.
            BACKEND.store(2, Ordering::Relaxed);
            let level = crate::arctis::adjust(increase)?;
            if increase {
                volume.SetMute(false, std::ptr::null())?;
            }
            return Ok(level);
        }
        BACKEND.store(1, Ordering::Relaxed);
        ensure!(max > min, "playback_endpoint_has_fixed_volume");
        let current = volume.GetMasterVolumeLevelScalar()?;
        ensure!(current.is_finite(), "invalid_master_volume");
        // Work in the Windows slider's normalized range. Hardware step tables
        // can round an adjacent step back to the current endpoint setting.
        volume.SetMasterVolumeLevelScalar(next_level(current, increase), std::ptr::null())?;
        if increase {
            volume.SetMute(false, std::ptr::null())?;
        }
        let after = volume.GetMasterVolumeLevelScalar()?;
        let target = next_level(current, increase);
        ensure!(
            target == current
                || if increase {
                    after > current
                } else {
                    after < current
                },
            "master_volume_unchanged"
        );
        Ok((after * 100.0).round() as u32)
    }
}

fn next_level(current: f32, increase: bool) -> f32 {
    (current + if increase { 0.02 } else { -0.02 }).clamp(0.0, 1.0)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn volume_moves_two_percentage_points_and_stops_at_limits() {
        assert!((next_level(0.5, true) - 0.52).abs() < 1e-6);
        assert!((next_level(0.5, false) - 0.48).abs() < 1e-6);
        assert!((next_level(1.0, false) - 0.98).abs() < 1e-6);
        assert_eq!(next_level(1.0, true), 1.0);
        assert_eq!(next_level(0.99, true), 1.0);
        assert_eq!(next_level(0.01, false), 0.0);
        assert_eq!(next_level(0.0, false), 0.0);
    }
    #[test]
    fn headset_control_requires_the_matching_fixed_default_endpoint() {
        assert!(fixed_arctis(
            "Headphones (Arctis Nova Pro Wireless)",
            0.0,
            0.0
        ));
        assert!(!fixed_arctis("Speakers", 0.0, 0.0));
        assert!(!fixed_arctis(
            "Line Out (Arctis Nova Pro Wireless)",
            0.0,
            0.0
        ));
        assert!(!fixed_arctis("SteelSeries Sonar - Gaming", 0.0, 0.0));
        assert!(!fixed_arctis(
            "Headphones (Arctis Nova Pro Wireless)",
            -96.0,
            0.0
        ));
    }
}
