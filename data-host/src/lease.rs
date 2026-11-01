//! Current installed runtime identity. No research receipts or credential parsing.
use anyhow::{Result, ensure};
use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::path::{Path, PathBuf};

#[derive(Deserialize, Clone)]
#[serde(deny_unknown_fields)]
pub struct OwnerReceipt {
    pub schema: u32,
    pub owner_pid: u32,
    pub owner_creation_filetime: u64,
    pub owner_executable_sha256: String,
    pub adapter_guid: String,
    pub adapter_mac: [u8; 6],
    pub controller_mac: [u8; 6],
    pub channel: u32,
    pub created_unix_ms: u64,
    pub config_sha256: String,
    pub session_id: String,
}

pub fn hash(bytes: &[u8]) -> String {
    format!("{:X}", Sha256::digest(bytes))
}
pub fn now_ms() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}
pub fn root() -> Result<PathBuf> {
    let local = std::env::var_os("LOCALAPPDATA")
        .ok_or_else(|| anyhow::anyhow!("local_app_data_unavailable"))?;
    Ok(PathBuf::from(local)
        .join("NVIDIA-SHIELD-P2570-Windows")
        .canonicalize()?)
}
pub fn in_root(path: &Path) -> Result<PathBuf> {
    let root = root()?;
    let path = path.canonicalize()?;
    ensure!(path.starts_with(root), "outside_installed_runtime");
    Ok(path)
}
pub fn bounded_read(path: &Path) -> Result<Vec<u8>> {
    let path = in_root(path)?;
    ensure!(
        path.metadata()?.len() <= 1024 * 1024,
        "oversized_runtime_state"
    );
    Ok(std::fs::read(path)?)
}
fn valid_mac(mac: [u8; 6]) -> bool {
    mac != [0; 6] && mac[0] & 1 == 0
}
impl OwnerReceipt {
    pub fn validate(&self, now: u64) -> Result<()> {
        ensure!(
            self.schema == 1 && self.owner_pid > 0 && self.owner_creation_filetime > 0,
            "invalid_owner_schema"
        );
        ensure!(self.channel == 36, "owner_not_channel36");
        ensure!(
            valid_mac(self.adapter_mac)
                && valid_mac(self.controller_mac)
                && self.adapter_mac != self.controller_mac,
            "invalid_peer_identity"
        );
        ensure!(
            self.created_unix_ms <= now && now - self.created_unix_ms <= 5000,
            "stale_owner_receipt"
        );
        for pin in [&self.owner_executable_sha256, &self.config_sha256] {
            ensure!(
                pin.len() == 64 && pin.bytes().all(|b| b.is_ascii_hexdigit()),
                "invalid_hash_pin"
            );
        }
        ensure!(
            self.session_id.len() == 32 && self.session_id.bytes().all(|b| b.is_ascii_hexdigit()),
            "invalid_session_id"
        );
        let guid = self.adapter_guid.trim_matches(['{', '}']);
        ensure!(
            self.adapter_guid.len() == 38
                && guid.len() == 36
                && [8, 13, 18, 23]
                    .into_iter()
                    .all(|i| guid.as_bytes()[i] == b'-')
                && guid
                    .bytes()
                    .enumerate()
                    .all(|(i, b)| [8, 13, 18, 23].contains(&i) || b.is_ascii_hexdigit()),
            "invalid_guid"
        );
        Ok(())
    }
    pub fn read(path: &Path) -> Result<Self> {
        let r: Self = serde_json::from_slice(&bounded_read(path)?)?;
        r.validate(now_ms())?;
        let session = root()?
            .join("sessions")
            .join(&r.session_id)
            .canonicalize()?;
        ensure!(
            in_root(path)?.starts_with(session),
            "receipt_outside_own_session"
        );
        ensure!(
            hash(&bounded_read(&root()?.join("config.json"))?)
                .eq_ignore_ascii_case(&r.config_sha256),
            "runtime_config_changed"
        );
        Ok(r)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn receipt() -> OwnerReceipt {
        OwnerReceipt {
            schema: 1,
            owner_pid: 12,
            owner_creation_filetime: 42,
            owner_executable_sha256: "A".repeat(64),
            adapter_guid: "{00000000-0000-0000-0000-000000000001}".into(),
            adapter_mac: [2; 6],
            controller_mac: [4; 6],
            channel: 36,
            created_unix_ms: 10000,
            config_sha256: "B".repeat(64),
            session_id: "c".repeat(32),
        }
    }
    #[test]
    fn current_schema_needs_fresh_exact_identity() {
        let r = receipt();
        assert!(r.validate(11000).is_ok());
        assert!(r.validate(16000).is_err());
        assert!(r.validate(9000).is_err());
        let mut r = receipt();
        r.controller_mac = r.adapter_mac;
        assert!(r.validate(11000).is_err());
        r = receipt();
        r.controller_mac = [1; 6];
        assert!(r.validate(11000).is_err());
        r = receipt();
        r.channel = 11;
        assert!(r.validate(11000).is_err());
    }
    #[test]
    fn rejects_invalid_guid_session_and_hash() {
        let mut r = receipt();
        r.session_id = "../other".into();
        assert!(r.validate(11000).is_err());
        r = receipt();
        r.adapter_guid = "{zzzzzzzz-0000-0000-0000-000000000001}".into();
        assert!(r.validate(11000).is_err());
        r = receipt();
        r.config_sha256 = "wrong".into();
        assert!(r.validate(11000).is_err());
    }
    #[test]
    fn historical_receipt_fields_are_rejected() {
        let json = r#"{"schema":1,"owner_pid":12,"owner_creation_filetime":42,"owner_executable_sha256":"A","adapter_guid":"B","adapter_mac":[2,2,2,2,2,2],"controller_mac":[4,4,4,4,4,4],"channel":36,"created_unix_ms":10000,"config_sha256":"C","session_id":"D","pair_receipt_path":"old"}"#;
        assert!(serde_json::from_str::<OwnerReceipt>(json).is_err());
    }
}
