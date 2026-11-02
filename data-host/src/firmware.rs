//! P2570 firmware response layout, checked against SHIELD's displayed version by
//! shieldd: shdlib/src/firmware.rs at 9eb7f473c9b12c1190a4250463a99b3bf6fdfc37.
//! The two undocumented bytes are deliberately not interpreted.

use std::fmt;

pub const VERSION_CMD: u8 = 0x08;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct FirmwareVersion([u8; 7]);

impl FirmwareVersion {
    pub fn decode(data: &[u8]) -> Option<Self> {
        if data.len() < 10 || data[0] != VERSION_CMD {
            return None;
        }
        let parts = [
            data[1], data[2], data[4], data[8], data[9], data[6], data[7],
        ];
        parts.iter().any(|&part| part != 0).then_some(Self(parts))
    }
}

impl fmt::Display for FirmwareVersion {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let [a, b, c, d, e, g, h] = self.0;
        write!(f, "{a}.{b:02}/{c}/{d}.{e:02}/{g}.{h:02}")
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn matches_version_checked_against_shield() {
        let answer = [0x08, 0x01, 0x60, 0xc0, 0x63, 0x06, 0x00, 0x20, 0x03, 0x47];
        assert_eq!(
            FirmwareVersion::decode(&answer).unwrap().to_string(),
            "1.96/99/3.71/0.32"
        );
        let mut padded = answer.to_vec();
        padded.resize(64, 0);
        assert_eq!(
            FirmwareVersion::decode(&padded),
            FirmwareVersion::decode(&answer)
        );
    }

    #[test]
    fn rejects_short_wrong_command_and_empty_answers() {
        for size in 0..10 {
            assert!(FirmwareVersion::decode(&[0x08; 10][..size]).is_none());
        }
        assert!(FirmwareVersion::decode(&[1; 10]).is_none());
        assert!(FirmwareVersion::decode(&[0x08, 0, 0, 0, 0, 0, 0, 0, 0, 0]).is_none());
    }
}
