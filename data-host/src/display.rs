//! Read-only calibration for the display under the pointer.
//!
//! Dimensions are physical pixels. GDI's monitor-specific millimetres are
//! driver-reported, not a claim that every driver exposes accurate EDID. When
//! unavailable, the returned size is explicitly labelled as a DPI estimate.
use anyhow::{Result, ensure};
use serde::Serialize;
use windows::{
    Win32::{
        Foundation::POINT,
        Graphics::Gdi::{
            CreateDCW, DeleteDC, GetDeviceCaps, GetMonitorInfoW, HDC, HORZSIZE,
            MONITOR_DEFAULTTONEAREST, MONITORINFOEXW, MonitorFromPoint, VERTSIZE,
        },
        UI::{
            HiDpi::{
                DPI_AWARENESS_CONTEXT, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2,
                SetThreadDpiAwarenessContext,
            },
            Shell::GetScaleFactorForMonitor,
            WindowsAndMessaging::GetCursorPos,
        },
    },
    core::{PCWSTR, w},
};

#[derive(Debug, Clone, Serialize)]
pub struct Calibration {
    pub width: u32,
    pub height: u32,
    pub dpi: u32,
    pub diagonal_inches: f64,
    #[serde(rename = "physical_size_source")]
    pub size_source: &'static str,
    pub width_mm: Option<u32>,
    pub height_mm: Option<u32>,
    pub default_gain: f64,
}

struct DpiContext(DPI_AWARENESS_CONTEXT);
impl DpiContext {
    fn enter() -> Result<Self> {
        let previous =
            unsafe { SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2) };
        ensure!(!previous.0.is_null(), "display_dpi_context_unavailable");
        Ok(Self(previous))
    }
}
impl Drop for DpiContext {
    fn drop(&mut self) {
        unsafe {
            SetThreadDpiAwarenessContext(self.0);
        }
    }
}
struct DisplayDc(HDC);
impl Drop for DisplayDc {
    fn drop(&mut self) {
        unsafe {
            let _ = DeleteDC(self.0);
        }
    }
}

fn from_metrics(width: i32, height: i32, dpi: u32, mm: Option<(i32, i32)>) -> Result<Calibration> {
    ensure!(
        width > 0 && height > 0 && (48..=960).contains(&dpi),
        "invalid_display_metrics"
    );
    let physical = mm.filter(|(w, h)| {
        // Bound implausible/placeholder driver values. Ultrawide and portrait
        // displays are both valid; rotation does not change their diagonal.
        *w >= 50 && *h >= 50 && *w <= 5000 && *h <= 5000
    });
    let (diagonal_inches, size_source) = match physical {
        Some((w, h)) => (f64::from(w).hypot(f64::from(h)) / 25.4, "driver-reported"),
        None => (
            f64::from(width).hypot(f64::from(height)) / f64::from(dpi),
            "dpi-estimate",
        ),
    };
    ensure!(
        diagonal_inches.is_finite() && diagonal_inches > 0.0,
        "invalid_display_size"
    );
    // Preserve the approved 32-inch baseline and scale pixel travel with
    // resolution. A modest physical-size correction keeps small/large screens
    // comfortable without allowing a dubious driver size to dominate speed.
    let resolution_factor = (f64::from(width) * f64::from(height) / (1920.0 * 1080.0))
        .sqrt()
        .clamp(0.75, 3.0);
    let size_factor = (32.0 / diagonal_inches).sqrt().clamp(0.8, 1.25);
    Ok(Calibration {
        width: width as u32,
        height: height as u32,
        dpi,
        diagonal_inches,
        size_source,
        width_mm: physical.map(|(w, _)| w as u32),
        height_mm: physical.map(|(_, h)| h as u32),
        default_gain: 4.0 * resolution_factor * size_factor,
    })
}

/// Queries display metadata only: no window creation, input injection, or setting changes.
pub fn calibration() -> Result<Calibration> {
    let _dpi = DpiContext::enter()?;
    unsafe {
        let mut point = POINT::default();
        GetCursorPos(&mut point)?;
        let monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        let mut info = MONITORINFOEXW::default();
        info.monitorInfo.cbSize = std::mem::size_of::<MONITORINFOEXW>() as u32;
        ensure!(
            GetMonitorInfoW(monitor, &mut info.monitorInfo).as_bool(),
            "display_info_unavailable"
        );
        // GetScaleFactorForMonitor is monitor-specific and does not depend on
        // the DPI-awareness pitfalls of GetDpiForMonitor on this thread.
        let scale = GetScaleFactorForMonitor(monitor)?.0;
        ensure!((50..=1000).contains(&scale), "display_scale_unavailable");
        let dpi = ((96 * scale + 50) / 100) as u32;
        let dc = CreateDCW(
            w!("DISPLAY"),
            PCWSTR(info.szDevice.as_ptr()),
            PCWSTR::null(),
            None,
        );
        let mm = if dc.0.is_null() {
            None
        } else {
            let dc = DisplayDc(dc);
            Some((
                GetDeviceCaps(Some(dc.0), HORZSIZE),
                GetDeviceCaps(Some(dc.0), VERTSIZE),
            ))
        };
        let bounds = info.monitorInfo.rcMonitor;
        from_metrics(
            bounds.right - bounds.left,
            bounds.bottom - bounds.top,
            dpi,
            mm,
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn resolution_scales_once_and_orientation_preserves_speed() {
        let hd = from_metrics(1920, 1080, 96, Some((708, 398))).unwrap();
        let uhd = from_metrics(3840, 2160, 192, Some((708, 398))).unwrap();
        let portrait = from_metrics(1080, 1920, 96, Some((398, 708))).unwrap();
        assert!((uhd.default_gain - hd.default_gain * 2.0).abs() < 1e-9);
        assert!((portrait.default_gain - hd.default_gain).abs() < 1e-9);
    }
    #[test]
    fn physical_size_changes_gain_conservatively() {
        let small = from_metrics(2560, 1440, 96, Some((531, 299))).unwrap();
        let baseline = from_metrics(2560, 1440, 96, Some((708, 398))).unwrap();
        let large = from_metrics(2560, 1440, 96, Some((1416, 796))).unwrap();
        assert!((baseline.default_gain - 16.0 / 3.0).abs() < 0.03);
        assert!(small.default_gain > baseline.default_gain);
        assert!(large.default_gain < baseline.default_gain);
        assert!(small.default_gain < baseline.default_gain * 1.26);
        assert!(large.default_gain >= 4.0 * (4.0 / 3.0) * 0.8);
    }
    #[test]
    fn missing_or_implausible_size_is_explicitly_estimated() {
        for mm in [None, Some((0, 0)), Some((1, 1)), Some((99999, 99999))] {
            let value = from_metrics(2560, 1440, 144, mm).unwrap();
            assert_eq!(value.size_source, "dpi-estimate");
            assert_eq!(value.width_mm, None);
            assert_eq!(value.height_mm, None);
            assert!((value.diagonal_inches - 2560f64.hypot(1440.0) / 144.0).abs() < 1e-9);
        }
    }
    #[test]
    fn known_size_does_not_apply_display_scale_twice() {
        let ordinary = from_metrics(2560, 1440, 96, Some((708, 398))).unwrap();
        let scaled = from_metrics(2560, 1440, 192, Some((708, 398))).unwrap();
        assert_eq!(ordinary.default_gain, scaled.default_gain);
        assert_eq!(scaled.size_source, "driver-reported");
    }
    #[test]
    fn invalid_resolution_or_dpi_is_rejected() {
        for (w, h, dpi) in [
            (0, 1440, 96),
            (2560, -1, 96),
            (2560, 1440, 0),
            (2560, 1440, 10000),
        ] {
            assert!(from_metrics(w, h, dpi, None).is_err());
        }
    }
    #[test]
    fn extremes_have_bounded_finite_gain() {
        for (w, h, mm) in [(640, 480, (5000, 5000)), (20000, 20000, (50, 50))] {
            let value = from_metrics(w, h, 96, Some(mm)).unwrap();
            assert!((2.4..=15.0).contains(&value.default_gain));
        }
    }
}
