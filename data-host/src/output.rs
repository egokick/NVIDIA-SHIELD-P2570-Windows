use crate::controller::Pad;
use anyhow::{Result, ensure};
use vigem_client::{Client, TargetId, XGamepad, Xbox360Wired};

/// ViGEm gamepad plus controller mouse, arrow keys and Windows volume output.
pub struct Output {
    mouse: crate::mouse::Mouse,
    keys: crate::keyboard::Keys,
    pad: Option<Xbox360Wired<Client>>,
    pending: Option<(XGamepad, bool)>,
    pub slot: Option<u32>,
    pub ready_reported: bool,
}
impl Output {
    pub fn new() -> Self {
        Self {
            mouse: crate::mouse::Mouse::default(),
            keys: crate::keyboard::Keys::default(),
            pad: None,
            pending: None,
            slot: None,
            ready_reported: false,
        }
    }
    pub fn plug(&mut self) -> Result<()> {
        if self.pad.is_some() {
            return Ok(());
        }
        let mut pad = Xbox360Wired::new(Client::connect()?, TargetId::XBOX360_WIRED);
        pad.plugin()?;
        // Store immediately: a later failure must still unplug this exact target.
        self.pad = Some(pad);
        let pad = self.pad.as_mut().unwrap();
        pad.wait_ready()?;
        let slot = pad.get_user_index()?;
        ensure!(slot <= 3, "invalid_xinput_slot");
        self.slot = Some(slot);
        submit_result(pad.update(&XGamepad::default()))?;
        Ok(())
    }
    pub fn pad(&mut self, p: &Pad) -> Result<bool> {
        self.mouse.pad(p)?;
        self.keys.report(p, std::time::Instant::now())?;
        self.pending = Some((crate::mapping::to_xgamepad(p), true));
        self.flush()
    }
    pub fn flush(&mut self) -> Result<bool> {
        let Some((report, live)) = self.pending else {
            return Ok(false);
        };
        if let Some(pad) = &mut self.pad {
            if submit_result(pad.update(&report))? {
                self.pending = None;
                return Ok(live);
            }
        }
        Ok(false)
    }
    pub fn tick(&mut self, now: std::time::Instant) -> Result<()> {
        self.keys.tick(now)
    }
    pub fn mouse(&mut self, t: &crate::controller::Trackpad) -> Result<()> {
        self.mouse.report(*t)
    }
    pub fn release(&mut self) -> Result<()> {
        let mouse = self.mouse.release();
        let keyboard = self.keys.release();
        self.ready_reported = false;
        self.pending = Some((XGamepad::default(), false));
        let gamepad = self.flush().map(|_| ());
        mouse.and(keyboard).and(gamepad)
    }
    pub fn close(&mut self) -> Result<()> {
        let release = self.release();
        let unplug = if let Some(mut pad) = self.pad.take() {
            pad.unplug().map_err(anyhow::Error::from)
        } else {
            Ok(())
        };
        self.slot = None;
        release.and(unplug)
    }
}
// ViGEmBus returns STATUS_NO_MORE_ENTRIES when there is no queued USB read.
// Its official client treats this as nonfatal. A later report gets another
// opportunity; it must not be counted as delivered to XInput yet.
fn submit_result(result: std::result::Result<(), vigem_client::Error>) -> Result<bool> {
    match result {
        Ok(()) => Ok(true),
        Err(vigem_client::Error::WinError(259)) => Ok(false),
        Err(error) => Err(error.into()),
    }
}
impl Drop for Output {
    fn drop(&mut self) {
        let _ = self.close();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn empty_usb_queue_is_not_a_dead_connection_or_a_delivered_report() {
        assert!(!submit_result(Err(vigem_client::Error::WinError(259))).unwrap());
        assert!(submit_result(Ok(())).unwrap());
        assert!(submit_result(Err(vigem_client::Error::WinError(5))).is_err());
    }
    #[test]
    fn media_and_trackpad_bits_do_not_become_gamepad_buttons() {
        let p = Pad {
            keys: crate::controller::key::VOL_UP
                | crate::controller::key::VOL_DOWN
                | crate::controller::key::MUTE,
            buttons: crate::controller::btn::SPURIOUS,
            ..Pad::default()
        };
        assert_eq!(crate::mapping::to_xgamepad(&p).buttons.raw, 0);
    }
    #[test]
    fn physical_pad_maps_a_and_left_stick() {
        let p = Pad {
            buttons: crate::controller::btn::A,
            lx: 65535,
            ly: 0,
            ..Pad::default()
        };
        let g = crate::mapping::to_xgamepad(&p);
        assert_eq!(g.buttons.raw, vigem_client::XButtons::A);
        assert_eq!((g.thumb_lx, g.thumb_ly), (32767, 32767));
    }
    #[test]
    fn release_requires_fresh_readiness_for_retained_slot() {
        let mut output = Output::new();
        output.slot = Some(2);
        output.ready_reported = true;
        output.release().unwrap();
        assert_eq!(output.slot, Some(2));
        assert!(!output.ready_reported);
    }
}
