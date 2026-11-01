use anyhow::{Result, bail, ensure};
use windows::Win32::Foundation::{CloseHandle, FILETIME, HANDLE, WAIT_TIMEOUT};
use windows::Win32::NetworkManagement::WiFi::{
    WLAN_INTF_OPCODE, WLAN_OPCODE_VALUE_TYPE, WlanCloseHandle, WlanFreeMemory, WlanOpenHandle,
    WlanQueryInterface,
};
use windows::Win32::System::Threading::{
    GetProcessTimes, OpenProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
    PROCESS_SYNCHRONIZE, QueryFullProcessImageNameW, WaitForSingleObject,
};
use windows::core::{GUID, PWSTR};

use crate::diagnostic::{Code, checkpoint};
use crate::lease::OwnerReceipt;

pub struct Owner(HANDLE);
impl Owner {
    pub fn open(r: &OwnerReceipt) -> Result<Self> {
        let handle = unsafe {
            OpenProcess(
                PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SYNCHRONIZE,
                false,
                r.owner_pid,
            )
        }?;
        let owner = Self(handle);
        let (mut creation, mut exit, mut kernel, mut user) = (
            FILETIME::default(),
            FILETIME::default(),
            FILETIME::default(),
            FILETIME::default(),
        );
        unsafe { GetProcessTimes(handle, &mut creation, &mut exit, &mut kernel, &mut user) }?;
        let ft = (u64::from(creation.dwHighDateTime) << 32) | u64::from(creation.dwLowDateTime);
        ensure!(
            ft == r.owner_creation_filetime && owner.alive(),
            Code::OwnerIdentity
        );
        let mut image = vec![0u16; 32768];
        let mut length = image.len() as u32;
        unsafe {
            QueryFullProcessImageNameW(
                handle,
                PROCESS_NAME_WIN32,
                PWSTR(image.as_mut_ptr()),
                &mut length,
            )
        }?;
        let path = std::path::PathBuf::from(String::from_utf16(&image[..length as usize])?);
        let path = checkpoint(crate::lease::in_root(&path), Code::OwnerImage)?;
        ensure!(
            path == crate::lease::root()?
                .join("bin")
                .join("owner")
                .join("ShieldControllerOwner.exe")
                .canonicalize()?,
            Code::OwnerImage
        );
        ensure!(
            crate::lease::hash(&checkpoint(
                std::fs::read(path).map_err(Into::into),
                Code::OwnerImage
            )?)
            .eq_ignore_ascii_case(&r.owner_executable_sha256),
            Code::OwnerImage
        );
        Ok(owner)
    }
    pub fn alive(&self) -> bool {
        unsafe { WaitForSingleObject(self.0, 0) == WAIT_TIMEOUT }
    }
}
impl Drop for Owner {
    fn drop(&mut self) {
        let _ = unsafe { CloseHandle(self.0) };
    }
}

pub fn verify_adapter(r: &OwnerReceipt) -> Result<()> {
    let all = checkpoint(crate::adapters::list(), Code::AdapterEnumeration)?;
    let active: Vec<_> = all.iter().filter(|a| a.is_wifi_direct() && a.up).collect();
    ensure!(!active.is_empty(), Code::ActiveGroupAbsent);
    ensure!(active.len() == 1, Code::ActiveGroupNotUnique);
    let adapter = active[0];
    ensure!(
        adapter.guid.eq_ignore_ascii_case(&r.adapter_guid) && adapter.mac == Some(r.adapter_mac),
        Code::AdapterIdentityChanged
    );
    let id = GUID::from_u128(
        u128::from_str_radix(
            &r.adapter_guid.trim_matches(['{', '}']).replace('-', ""),
            16,
        )
        .map_err(|_| anyhow::anyhow!(Code::AdapterGuidInvalid))?,
    );
    let (mut negotiated, mut handle) = (0, HANDLE::default());
    ensure!(
        unsafe { WlanOpenHandle(2, None, &mut negotiated, &mut handle) } == 0,
        Code::WlanOpen
    );
    let result = (|| {
        for (opcode, expected) in [(6, 1), (8, r.channel)] {
            let (mut size, mut data, mut kind) =
                (0, std::ptr::null_mut(), WLAN_OPCODE_VALUE_TYPE::default());
            let rc = unsafe {
                WlanQueryInterface(
                    handle,
                    &id,
                    WLAN_INTF_OPCODE(opcode),
                    None,
                    &mut size,
                    &mut data,
                    Some(&mut kind),
                )
            };
            let value = if rc == 0 && size == 4 && !data.is_null() {
                Some(unsafe { data.cast::<u32>().read_unaligned() })
            } else {
                None
            };
            if !data.is_null() {
                unsafe { WlanFreeMemory(data) };
            }
            if value.is_none() {
                bail!(if opcode == 6 {
                    Code::WlanStateQuery
                } else {
                    Code::WlanChannelQuery
                });
            }
            if value != Some(expected) {
                bail!(if opcode == 6 {
                    Code::WlanStateChanged
                } else {
                    Code::WlanChannelChanged
                });
            }
        }
        Ok(())
    })();
    unsafe { WlanCloseHandle(handle, None) };
    result
}
