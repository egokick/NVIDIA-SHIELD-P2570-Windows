//! Execute only a file explicitly selected for a controller binding.
use anyhow::{Result, ensure};
pub(crate) fn run(value: &str) -> Result<()> {
    use std::{
        os::windows::process::CommandExt,
        path::Path,
        process::{Command, Stdio},
    };
    use windows::Win32::System::{
        SystemInformation::GetSystemDirectoryW, Threading::CREATE_NO_WINDOW,
    };
    crate::profile::validate_launch_path(value)?;
    let path = Path::new(value);
    ensure!(path.is_file(), "controls_launch_missing");
    let extension = path
        .extension()
        .and_then(|x| x.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    let mut directory = [0u16; 32768];
    let length = unsafe { GetSystemDirectoryW(Some(&mut directory)) } as usize;
    ensure!(
        length > 0 && length < directory.len(),
        "controls_system_directory"
    );
    let system = std::path::PathBuf::from(String::from_utf16_lossy(&directory[..length]));
    let mut process = match extension.as_str() {
        "ps1" => {
            let mut p = Command::new(system.join("WindowsPowerShell/v1.0/powershell.exe"));
            // This process may run the local script explicitly selected by the
            // user. RemoteSigned still checks downloaded scripts and obeys
            // Group Policy; it neither changes machine policy nor unblocks files.
            p.arg("-NoProfile")
                .arg("-NonInteractive")
                .arg("-ExecutionPolicy")
                .arg("RemoteSigned")
                .arg("-File")
                .arg(path)
                .creation_flags(CREATE_NO_WINDOW.0);
            p
        }
        "cmd" | "bat" => {
            let mut p = Command::new(system.join("cmd.exe"));
            p.raw_arg(format!("/d /v:off /s /c \"\"{value}\"\""))
                .creation_flags(CREATE_NO_WINDOW.0);
            p
        }
        "exe" => Command::new(path),
        _ => anyhow::bail!("controls_launch_type"),
    };
    process
        .current_dir(
            path.parent()
                .ok_or_else(|| anyhow::anyhow!("controls_launch_parent"))?,
        )
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .spawn()
        .map_err(|_| anyhow::anyhow!("controls_launch_failed"))?;
    Ok(())
}
