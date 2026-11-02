//! Network adapter lookup through GetAdaptersAddresses.

use std::time::{Duration, Instant};

use anyhow::{Result, bail};
use ozproto::Mac;
use windows::Win32::Foundation::{ERROR_BUFFER_OVERFLOW, ERROR_SUCCESS, HANDLE};
use windows::Win32::NetworkManagement::IpHelper::{
    GAA_FLAG_INCLUDE_ALL_INTERFACES, GAA_FLAG_SKIP_ANYCAST, GAA_FLAG_SKIP_DNS_SERVER,
    GAA_FLAG_SKIP_MULTICAST, GAA_FLAG_SKIP_UNICAST, GetAdaptersAddresses, GetIfEntry2,
    IF_TYPE_IEEE80211, IP_ADAPTER_ADDRESSES_LH, MIB_IF_ROW2,
};
use windows::Win32::NetworkManagement::Ndis::IfOperStatusUp;
use windows::Win32::NetworkManagement::WiFi::{
    DOT11_OPERATION_MODE_WFD_GROUP_OWNER, WlanCloseHandle, WlanFreeMemory, WlanOpenHandle,
    WlanQueryInterface, wlan_intf_opcode_current_operation_mode,
};
use windows::Win32::Networking::WinSock::AF_UNSPEC;
use windows::core::GUID;

struct WifiRoles(HANDLE);

impl WifiRoles {
    fn open() -> Result<Self> {
        let (mut version, mut handle) = (0, HANDLE::default());
        let rc = unsafe { WlanOpenHandle(2, None, &mut version, &mut handle) };
        if rc != 0 {
            bail!("WlanOpenHandle failed while reading interface roles: {rc}");
        }
        Ok(Self(handle))
    }

    fn group_owner(&self, guid: &str) -> bool {
        let Ok(value) = u128::from_str_radix(&guid.trim_matches(['{', '}']).replace('-', ""), 16)
        else {
            return false;
        };
        let id = GUID::from_u128(value);
        let (mut size, mut data) = (0, std::ptr::null_mut());
        let rc = unsafe {
            WlanQueryInterface(
                self.0,
                &id,
                wlan_intf_opcode_current_operation_mode,
                None,
                &mut size,
                &mut data,
                None,
            )
        };
        let mode = if rc == 0 && size == 4 && !data.is_null() {
            Some(unsafe { data.cast::<u32>().read_unaligned() })
        } else {
            None
        };
        if !data.is_null() {
            unsafe { WlanFreeMemory(data) };
        }
        group_owner_mode(mode)
    }
}

impl Drop for WifiRoles {
    fn drop(&mut self) {
        unsafe { WlanCloseHandle(self.0, None) };
    }
}

fn group_owner_mode(mode: Option<u32>) -> bool {
    mode == Some(DOT11_OPERATION_MODE_WFD_GROUP_OWNER)
}

#[derive(Debug, Clone)]
pub struct Adapter {
    /// `{GUID}`, braces included; what Npcap device names are built from
    pub guid: String,
    pub friendly_name: String,
    pub description: String,
    pub mac: Option<Mac>,
    pub up: bool,
    pub group_owner: bool,
}

impl Adapter {
    pub fn is_wifi_direct(&self) -> bool {
        self.group_owner
    }

    /// Same GUID w/o braces, for comparing against WinRT adapter IDs
    pub fn bare_guid(&self) -> &str {
        self.guid.trim_start_matches('{').trim_end_matches('}')
    }
}

pub fn list() -> Result<Vec<Adapter>> {
    let roles = WifiRoles::open()?;
    let flags = GAA_FLAG_INCLUDE_ALL_INTERFACES
        | GAA_FLAG_SKIP_UNICAST
        | GAA_FLAG_SKIP_ANYCAST
        | GAA_FLAG_SKIP_MULTICAST
        | GAA_FLAG_SKIP_DNS_SERVER;
    let mut size: u32 = 16 * 1024;
    // u64 backing keeps the structs aligned
    let mut buf: Vec<u64>;
    loop {
        buf = vec![0u64; (size as usize).div_ceil(8)];
        let head = buf.as_mut_ptr().cast::<IP_ADAPTER_ADDRESSES_LH>();
        // SAFETY: buf holds `size` writable bytes, aligned for the struct
        let rc =
            unsafe { GetAdaptersAddresses(AF_UNSPEC.0 as u32, flags, None, Some(head), &mut size) };
        match rc {
            x if x == ERROR_SUCCESS.0 => break,
            x if x == ERROR_BUFFER_OVERFLOW.0 => continue,
            x => bail!("GetAdaptersAddresses failed: {x}"),
        }
    }

    let mut out = Vec::new();
    let mut p = buf.as_ptr().cast::<IP_ADAPTER_ADDRESSES_LH>();
    while !p.is_null() {
        // SAFETY: p walks the list GetAdaptersAddresses built inside buf, which
        // is still alive; the strings it points at live in the same buffer.
        let a = unsafe { &*p };
        let (guid, friendly_name, description) = unsafe {
            (
                a.AdapterName.to_string().unwrap_or_default(),
                a.FriendlyName.to_string().unwrap_or_default(),
                a.Description.to_string().unwrap_or_default(),
            )
        };
        // INCLUDE_ALL_INTERFACES also includes WFP/QoS filter stack entries
        // which repeat the virtual adapter's description and MAC. They are
        // not independent capture adapters. Use the documented flag rather
        // than trying to recognize localized filter names.
        // WiFiCx ports can share the physical radio's description. Never
        // identify a GO by an English or vendor-specific display name.
        // Only active IEEE 802.11 ports need a WLAN role query. Keep checking
        // every wireless port so multiple GOs remain an error.
        let up = a.OperStatus == IfOperStatusUp;
        let group_owner = up && a.IfType == IF_TYPE_IEEE80211 && roles.group_owner(&guid);
        if group_owner {
            let mut row = MIB_IF_ROW2 {
                InterfaceLuid: a.Luid,
                ..Default::default()
            };
            let rc = unsafe { GetIfEntry2(&mut row) };
            if rc != ERROR_SUCCESS {
                bail!("GetIfEntry2 failed for Wi-Fi Direct port: {}", rc.0);
            }
            if row.InterfaceAndOperStatusFlags._bitfield & (1 << 1) != 0 {
                p = a.Next;
                continue;
            }
        }
        let mac =
            (a.PhysicalAddressLength == 6).then(|| a.PhysicalAddress[..6].try_into().unwrap());
        out.push(Adapter {
            guid,
            friendly_name,
            description,
            mac,
            up,
            group_owner,
        });
        p = a.Next;
    }
    Ok(out)
}

pub fn by_name(friendly_name: &str) -> Result<Adapter> {
    match list()?
        .into_iter()
        .find(|a| a.friendly_name == friendly_name)
    {
        Some(a) => Ok(a),
        None => bail!("no network adapter named {friendly_name:?}"),
    }
}

/// Select the virtual port brought up by this publisher. A virtual GO can
/// have a different MAC from its physical radio (including the RTL8852CE).
fn newly_up_go(before: &[Adapter], after: Vec<Adapter>) -> Result<Option<Adapter>> {
    let mut candidates = after.into_iter().filter(|a| {
        a.is_wifi_direct()
            && a.up
            && !before
                .iter()
                .any(|old| old.guid.eq_ignore_ascii_case(&a.guid) && old.up)
    });
    let first = candidates.next();
    if candidates.next().is_some() {
        bail!(
            "multiple Wi-Fi Direct ports became active; stop other Wi-Fi Direct sessions and retry"
        );
    }
    if let Some(a) = &first {
        if a.mac.is_none() {
            bail!("new Wi-Fi Direct port has no Ethernet MAC");
        }
    }
    Ok(first)
}

pub fn wait_for_go(before: &[Adapter], timeout: Duration) -> Result<Adapter> {
    ensure_no_active_go(before)?;
    let deadline = Instant::now() + timeout;
    let mut stable: Option<(String, Instant)> = None;
    loop {
        // Windows can temporarily mark both virtual ports up while the GO
        // starts. Only a unique, stable candidate is safe to return.
        let candidate = newly_up_go(before, list()?);
        if let Ok(Some(a)) = &candidate {
            match &stable {
                Some((guid, since)) if guid.eq_ignore_ascii_case(&a.guid) => {
                    if since.elapsed() >= Duration::from_secs(1) {
                        return Ok(a.clone());
                    }
                }
                _ => stable = Some((a.guid.clone(), Instant::now())),
            }
        } else {
            stable = None;
        }
        if Instant::now() > deadline {
            if let Err(error) = candidate {
                return Err(error);
            }
            bail!("no new Wi-Fi Direct port became active after starting the group");
        }
        std::thread::sleep(Duration::from_millis(250));
    }
}

pub fn ensure_no_active_go(adapters: &[Adapter]) -> Result<()> {
    if adapters.iter().any(|a| a.is_wifi_direct() && a.up) {
        bail!(
            "a Wi-Fi Direct port is already active; stop Mobile Hotspot and other Wi-Fi Direct sessions before starting shieldd"
        );
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn port(id: &str, mode: u32, up: bool) -> Adapter {
        Adapter {
            guid: id.into(),
            friendly_name: "Wi-Fi".into(),
            description: "MediaTek Wi-Fi 7 MT7925 Wireless LAN Card".into(),
            mac: Some([2, 1, 2, 3, 4, 5]),
            up,
            group_owner: group_owner_mode(Some(mode)),
        }
    }

    #[test]
    fn wificx_ports_with_identical_descriptions_use_reported_roles() {
        let station = port("physical", 4, true);
        let before = vec![station.clone(), port("go", 32, false)];
        ensure_no_active_go(&before).unwrap();
        let after = vec![station, port("go", 32, true)];
        assert!(ensure_no_active_go(&after).is_err());
        assert_eq!(newly_up_go(&before, after).unwrap().unwrap().guid, "go");
        assert!(!group_owner_mode(None));
        assert!(!group_owner_mode(Some(16)));
        assert!(!group_owner_mode(Some(64)));
    }

    #[test]
    fn multiple_reported_group_owners_remain_ambiguous() {
        assert!(newly_up_go(&[], vec![port("one", 32, true), port("two", 32, true)]).is_err());
    }
}
