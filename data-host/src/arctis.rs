//! Hardware headphone volume for the fixed-volume Arctis Nova Pro Wireless.
//! Caller must first verify that the current default audio endpoint is this headset.
//! Protocol: https://github.com/hughes/GGWP/blob/main/FINDINGS.md
//! Only state query 06 20 and volatile headphone-volume set 06 21 00 XX are sent.
use anyhow::{Result, ensure};
use std::{
    sync::atomic::{AtomicBool, Ordering},
    time::{Duration, Instant},
};
use windows::{
    Win32::{
        Devices::HumanInterfaceDevice::{
            HIDD_ATTRIBUTES, HIDP_CAPS, HIDP_STATUS_SUCCESS, HidD_FlushQueue,
            HidD_FreePreparsedData, HidD_GetAttributes, HidD_GetPreparsedData, HidP_GetCaps,
            PHIDP_PREPARSED_DATA,
        },
        Foundation::{
            CloseHandle, ERROR_IO_INCOMPLETE, ERROR_IO_PENDING, GENERIC_READ, GENERIC_WRITE, HANDLE,
        },
        Storage::FileSystem::{
            CreateFileW, FILE_FLAG_OVERLAPPED, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING,
            ReadFile, WriteFile,
        },
        System::{
            IO::{CancelIoEx, GetOverlappedResult, GetOverlappedResultEx, OVERLAPPED},
            Threading::CreateEventW,
        },
    },
    core::PCWSTR,
};

const MAX_ATTENUATION: u8 = 56;
static IO_POISONED: AtomicBool = AtomicBool::new(false);

struct Handle(HANDLE);
impl Drop for Handle {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}
struct Io {
    overlapped: OVERLAPPED,
    buffer: Vec<u8>,
    _event: Handle,
}
impl Io {
    fn new(buffer: Vec<u8>) -> Result<Box<Self>> {
        unsafe {
            let event = Handle(CreateEventW(None, true, false, None)?);
            Ok(Box::new(Self {
                overlapped: OVERLAPPED {
                    hEvent: event.0,
                    ..Default::default()
                },
                buffer,
                _event: event,
            }))
        }
    }
}

pub struct Headset {
    handle: Handle,
    output_length: usize,
    input_length: usize,
}
impl Headset {
    pub fn open() -> Result<Self> {
        ensure!(
            !IO_POISONED.load(Ordering::Acquire),
            "arctis_io_restart_required"
        );
        let api =
            hidapi::HidApi::new().map_err(|_| anyhow::anyhow!("arctis_enumeration_failed"))?;
        let candidates: Vec<_> = api
            .device_list()
            .filter(|d| {
                d.vendor_id() == 0x1038
                    && d.product_id() == 0x12e0
                    && d.interface_number() == 4
                    && d.usage_page() == 0xffc0
            })
            .collect();
        ensure!(
            candidates.len() == 1,
            "arctis_expected_one_control_interface"
        );
        let path = candidates[0]
            .path()
            .to_str()
            .map_err(|_| anyhow::anyhow!("arctis_invalid_path"))?;
        let path: Vec<u16> = path.encode_utf16().chain(Some(0)).collect();
        unsafe {
            let handle = Handle(CreateFileW(
                PCWSTR(path.as_ptr()),
                GENERIC_READ.0 | GENERIC_WRITE.0,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                None,
                OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED,
                None,
            )?);
            let mut attributes = HIDD_ATTRIBUTES {
                Size: std::mem::size_of::<HIDD_ATTRIBUTES>() as u32,
                ..Default::default()
            };
            ensure!(
                HidD_GetAttributes(handle.0, &mut attributes),
                "arctis_attributes_failed"
            );
            ensure!(
                attributes.VendorID == 0x1038 && attributes.ProductID == 0x12e0,
                "arctis_identity_changed"
            );
            let mut preparsed = PHIDP_PREPARSED_DATA::default();
            ensure!(
                HidD_GetPreparsedData(handle.0, &mut preparsed),
                "arctis_descriptor_failed"
            );
            let mut caps = HIDP_CAPS::default();
            let status = HidP_GetCaps(preparsed, &mut caps);
            HidD_FreePreparsedData(preparsed);
            ensure!(status == HIDP_STATUS_SUCCESS, "arctis_caps_failed");
            ensure!(caps.UsagePage == 0xffc0, "arctis_usage_changed");
            ensure!(
                matches!(caps.OutputReportByteLength, 31 | 64) && caps.InputReportByteLength == 64,
                "arctis_unexpected_report_lengths"
            );
            Ok(Self {
                handle,
                output_length: usize::from(caps.OutputReportByteLength),
                input_length: usize::from(caps.InputReportByteLength),
            })
        }
    }
    pub fn report_lengths(&self) -> (usize, usize) {
        (self.output_length, self.input_length)
    }
    fn transfer(&mut self, buffer: Vec<u8>, write: bool, wait_ms: u32) -> Result<Vec<u8>> {
        ensure!(
            !IO_POISONED.load(Ordering::Acquire),
            "arctis_io_restart_required"
        );
        let mut io = Io::new(buffer)?;
        let mut count = 0u32;
        unsafe {
            let started = if write {
                WriteFile(
                    self.handle.0,
                    Some(&io.buffer),
                    None,
                    Some(&mut io.overlapped),
                )
            } else {
                ReadFile(
                    self.handle.0,
                    Some(&mut io.buffer),
                    None,
                    Some(&mut io.overlapped),
                )
            };
            if let Err(error) = started {
                ensure!(
                    error.code() == ERROR_IO_PENDING.to_hresult(),
                    "arctis_io_start_failed"
                );
            }
            if GetOverlappedResultEx(
                self.handle.0,
                &io.overlapped,
                &mut count,
                wait_ms.min(125),
                false,
            )
            .is_err()
            {
                let _ = CancelIoEx(self.handle.0, Some(&io.overlapped));
                let _ = GetOverlappedResultEx(self.handle.0, &io.overlapped, &mut count, 25, false);
                if let Err(error) =
                    GetOverlappedResult(self.handle.0, &io.overlapped, &mut count, false)
                {
                    if error.code() == ERROR_IO_INCOMPLETE.to_hresult() {
                        // A failed kernel cancellation cannot safely release its buffers.
                        // Keep their stable heap addresses alive; disable all further HID
                        // operations until process restart rather than leak on every repeat.
                        IO_POISONED.store(true, Ordering::Release);
                        std::mem::forget(io);
                    }
                }
                anyhow::bail!("arctis_io_timeout_or_failed");
            }
        }
        ensure!(
            count as usize <= io.buffer.len(),
            "arctis_invalid_transfer_length"
        );
        if write {
            ensure!(count as usize == io.buffer.len(), "arctis_short_write");
        }
        io.buffer.truncate(count as usize);
        Ok(std::mem::take(&mut io.buffer))
    }
    fn write_command(&mut self, command: &[u8]) -> Result<()> {
        let packet = packet(command, self.output_length)?;
        self.transfer(packet, true, 125)?;
        Ok(())
    }
    pub fn query(&mut self) -> Result<u8> {
        ensure!(
            unsafe { HidD_FlushQueue(self.handle.0) },
            "arctis_queue_flush_failed"
        );
        self.write_command(&[0x06, 0x20])?;
        let deadline = Instant::now() + Duration::from_millis(125);
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            ensure!(!remaining.is_zero(), "arctis_state_timeout");
            let report = self.transfer(
                vec![0; self.input_length],
                false,
                (remaining.as_millis() as u32).max(1),
            )?;
            if let Some(raw) = parse_state(&report)? {
                return Ok(raw);
            }
        }
    }
    pub fn set_verified(&mut self, raw: u8) -> Result<u8> {
        ensure!(raw <= MAX_ATTENUATION, "arctis_volume_out_of_range");
        self.write_command(&[0x06, 0x21, 0x00, raw])?;
        let observed = self.query()?;
        ensure!(observed == raw, "arctis_volume_readback_mismatch");
        Ok(observed)
    }
}
fn packet(command: &[u8], length: usize) -> Result<Vec<u8>> {
    ensure!(matches!(length, 31 | 64), "arctis_invalid_output_length");
    ensure!(
        command == [0x06, 0x20]
            || (command.len() == 4
                && command[..3] == [0x06, 0x21, 0x00]
                && command[3] <= MAX_ATTENUATION),
        "arctis_command_rejected"
    );
    let mut packet = vec![0; length];
    packet[..command.len()].copy_from_slice(command);
    Ok(packet)
}
fn parse_state(report: &[u8]) -> Result<Option<u8>> {
    if !report.starts_with(&[0x06, 0x20]) {
        return Ok(None);
    }
    ensure!(
        report.len() == 64 && report[2] == 0 && report[3] <= MAX_ATTENUATION,
        "arctis_invalid_state_response"
    );
    Ok(Some(report[3]))
}
fn target(raw: u8, increase: bool) -> Result<u8> {
    ensure!(raw <= MAX_ATTENUATION, "arctis_volume_out_of_range");
    Ok(if increase {
        raw.saturating_sub(1)
    } else {
        raw.saturating_add(1).min(MAX_ATTENUATION)
    })
}
pub fn percent(raw: u8) -> u32 {
    (u32::from(MAX_ATTENUATION.saturating_sub(raw)) * 100 + 28) / 56
}
pub fn adjust(increase: bool) -> Result<u32> {
    let mut headset = Headset::open()?;
    let current = headset.query()?;
    let target = target(current, increase)?;
    if target != current {
        headset.set_verified(target)?;
    }
    Ok(percent(target))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn only_documented_commands_are_accepted() {
        let query = packet(&[6, 0x20], 31).unwrap();
        assert_eq!(&query[..2], &[6, 0x20]);
        assert!(query[2..].iter().all(|b| *b == 0));
        let set = packet(&[6, 0x21, 0, 56], 64).unwrap();
        assert_eq!(&set[..4], &[6, 0x21, 0, 56]);
        assert!(set[4..].iter().all(|b| *b == 0));
        for command in [
            &[6, 9][..],
            &[6, 0x25, 0, 1],
            &[6, 0x21, 1, 1],
            &[6, 0x21, 0, 57],
            &[6, 0x20, 0],
        ] {
            assert!(packet(command, 31).is_err());
        }
        assert!(packet(&[6, 0x20], 32).is_err());
    }
    #[test]
    fn state_validates_framing_bounds_and_ignores_other_reports() {
        let mut report = [0u8; 64];
        report[..4].copy_from_slice(&[6, 0x20, 0, 56]);
        assert_eq!(parse_state(&report).unwrap(), Some(56));
        assert!(parse_state(&report[..4]).is_err());
        report[3] = 57;
        assert!(parse_state(&report).is_err());
        report[3] = 1;
        report[2] = 1;
        assert!(parse_state(&report).is_err());
        assert_eq!(parse_state(&[6, 0xb0, 0, 1]).unwrap(), None);
    }
    #[test]
    fn attenuation_direction_and_bounds() {
        assert_eq!(target(10, true).unwrap(), 9);
        assert_eq!(target(10, false).unwrap(), 11);
        assert_eq!(target(0, true).unwrap(), 0);
        assert_eq!(target(56, false).unwrap(), 56);
        assert!(target(57, false).is_err());
        assert_eq!(percent(0), 100);
        assert_eq!(percent(28), 50);
        assert_eq!(percent(56), 0);
    }
}
