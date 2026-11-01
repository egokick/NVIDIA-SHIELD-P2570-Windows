//! Static diagnostic categories only. Never serialize anyhow chains/native text.
use anyhow::Result;
use std::sync::atomic::{AtomicU8, Ordering};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
#[repr(u8)]
pub enum Code {
    OutputLog = 1,
    OwnerReceipt,
    PeerIdentity,
    OwnerProcess,
    OwnerIdentity,
    OwnerImage,
    AdapterEnumeration,
    ActiveGroupAbsent,
    ActiveGroupNotUnique,
    AdapterIdentityChanged,
    AdapterGuidInvalid,
    WlanOpen,
    WlanStateQuery,
    WlanChannelQuery,
    WlanStateChanged,
    WlanChannelChanged,
    OwnerLeaseEnded,
    NpcapInit,
    NpcapOpenTx,
    NpcapOpenRx,
    TxUnexpectedPeer,
    TxSend,
    RxRead,
    RxQueueFull,
    TransportThreadUnknown,
    OwnerExited,
    RxThreadClosed,
    TxUnexpectedOutput,
    TxQueueFull,
    OutputRelease,
    OutputPlug,
    OutputPad,
    OzmoConnectRejected,
    OutputClose,
    TransportThreadPanicked,
}
impl std::fmt::Display for Code {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.name())
    }
}
impl std::error::Error for Code {}
impl Code {
    pub fn name(self) -> &'static str {
        match self {
            Self::OutputLog => "output_log",
            Self::OwnerReceipt => "owner_receipt",
            Self::PeerIdentity => "peer_identity",
            Self::OwnerProcess => "owner_process",
            Self::OwnerIdentity => "owner_identity",
            Self::OwnerImage => "owner_image",
            Self::AdapterEnumeration => "adapter_enumeration",
            Self::ActiveGroupAbsent => "active_group_absent",
            Self::ActiveGroupNotUnique => "active_group_not_unique",
            Self::AdapterIdentityChanged => "adapter_identity_changed",
            Self::AdapterGuidInvalid => "adapter_guid_invalid",
            Self::WlanOpen => "wlan_open",
            Self::WlanStateQuery => "wlan_state_query",
            Self::WlanChannelQuery => "wlan_channel_query",
            Self::WlanStateChanged => "wlan_state_changed",
            Self::WlanChannelChanged => "wlan_channel_changed",
            Self::OwnerLeaseEnded => "owner_lease_ended",
            Self::NpcapInit => "npcap_init",
            Self::NpcapOpenTx => "npcap_open_tx",
            Self::NpcapOpenRx => "npcap_open_rx",
            Self::TxUnexpectedPeer => "tx_unexpected_peer",
            Self::TxSend => "tx_send",
            Self::RxRead => "rx_read",
            Self::RxQueueFull => "rx_queue_full",
            Self::TransportThreadUnknown => "transport_thread_unknown",
            Self::OwnerExited => "owner_exited",
            Self::RxThreadClosed => "rx_thread_closed",
            Self::TxUnexpectedOutput => "tx_unexpected_output",
            Self::TxQueueFull => "tx_queue_full",
            Self::OutputRelease => "output_release",
            Self::OutputPlug => "output_plug",
            Self::OutputPad => "output_pad",
            Self::OzmoConnectRejected => "ozmo_connect_rejected",
            Self::OutputClose => "output_close",
            Self::TransportThreadPanicked => "transport_thread_panicked",
        }
    }
}
pub fn checkpoint<T>(result: Result<T>, code: Code) -> Result<T> {
    result.map_err(|e| {
        if e.downcast_ref::<Code>().is_some() {
            e
        } else {
            e.context(code)
        }
    })
}
pub fn category(error: &anyhow::Error) -> &'static str {
    error
        .downcast_ref::<Code>()
        .map(|c| c.name())
        .unwrap_or("unclassified_redacted")
}
pub fn record_first(slot: &AtomicU8, code: Code) {
    let _ = slot.compare_exchange(0, code as u8, Ordering::Relaxed, Ordering::Relaxed);
}
pub fn transport_code(slot: &AtomicU8) -> Code {
    match slot.load(Ordering::Relaxed) {
        v if v == Code::TxUnexpectedPeer as u8 => Code::TxUnexpectedPeer,
        v if v == Code::TxSend as u8 => Code::TxSend,
        v if v == Code::RxRead as u8 => Code::RxRead,
        v if v == Code::RxQueueFull as u8 => Code::RxQueueFull,
        _ => Code::TransportThreadUnknown,
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn native_text_cannot_escape_category() {
        let raw = anyhow::anyhow!("secret password, raw device GUID and packet bytes");
        assert_eq!(category(&raw), "unclassified_redacted");
        let wrapped = checkpoint::<()>(Err(raw), Code::WlanChannelQuery).unwrap_err();
        let wrapped = checkpoint::<()>(Err(wrapped), Code::OwnerProcess).unwrap_err();
        assert_eq!(category(&wrapped), "wlan_channel_query");
    }
    #[test]
    fn first_transport_cause_is_retained() {
        let slot = AtomicU8::new(0);
        assert_eq!(transport_code(&slot), Code::TransportThreadUnknown);
        record_first(&slot, Code::RxRead);
        record_first(&slot, Code::TxSend);
        assert_eq!(transport_code(&slot), Code::RxRead);
    }
}
