use crate::controller::Pad;
use anyhow::{Result, ensure};
use vigem_client::{Client, TargetId, XGamepad, Xbox360Wired};

/// Only ViGEm gamepad output. No global mouse, keyboard, media, audio or rumble.
pub struct Output {
    pad: Option<Xbox360Wired<Client>>,
    pub slot: Option<u32>,
    pub ready_reported: bool,
}
impl Output {
    pub fn new() -> Self {
        Self {
            pad: None,
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
        pad.update(&XGamepad::default())?;
        Ok(())
    }
    pub fn pad(&mut self, p: &Pad) -> Result<()> {
        if let Some(pad) = &mut self.pad {
            pad.update(&crate::mapping::to_xgamepad(p))?;
        }
        Ok(())
    }
    pub fn release(&mut self) -> Result<()> {
        self.ready_reported = false;
        if let Some(pad) = &mut self.pad {
            pad.update(&XGamepad::default())?;
        }
        Ok(())
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
impl Drop for Output {
    fn drop(&mut self) {
        let _ = self.close();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
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
