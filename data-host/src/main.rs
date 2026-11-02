//! SHIELD gamepad and touchpad transport, supervised by the installed hotspot owner.
#![allow(dead_code)]
use anyhow::{Result, ensure};
use clap::Parser;
use ozproto::{Config, Event, Host, Mac};
use serde_json::json;
use std::{
    io::{Read, Seek, SeekFrom, Write},
    path::{Path, PathBuf},
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU8, AtomicU64, Ordering},
        mpsc,
    },
    time::{Duration, Instant},
};
mod adapters;
mod controller;
mod diagnostic;
use diagnostic::{Code, checkpoint};
mod keyboard;
mod lease;
mod mapping;
mod mouse;
mod npcap;
mod output;
mod platform;

#[derive(Parser)]
struct Args {
    #[arg(long)]
    run: bool,
    #[arg(long, requires = "run")]
    owner_receipt: Option<PathBuf>,
    #[arg(long, requires = "run")]
    output: Option<PathBuf>,
}

struct Log {
    file: std::fs::File,
    start: Instant,
    bytes: usize,
}
impl Log {
    fn new(dir: &Path) -> Result<Self> {
        ensure!(!dir.exists(), "output_must_be_new");
        let parent = lease::in_root(
            dir.parent()
                .ok_or_else(|| anyhow::anyhow!("output_parent_missing"))?,
        )?;
        ensure!(
            parent.starts_with(lease::root()?.join("sessions")),
            "output_outside_sessions"
        );
        let dir = parent.join(
            dir.file_name()
                .ok_or_else(|| anyhow::anyhow!("output_name_missing"))?,
        );
        std::fs::create_dir(&dir)?;
        let file = std::fs::OpenOptions::new()
            .create_new(true)
            .write(true)
            .open(dir.join("events.jsonl"))?;
        Ok(Self {
            file,
            start: Instant::now(),
            bytes: 0,
        })
    }
    fn event(&mut self, stage: &str, detail: serde_json::Value) -> Result<()> {
        checkpoint(
            (|| {
                let line = serde_json::to_string(
                    &json!({"stage":stage,"elapsed_ms":self.start.elapsed().as_millis(),"detail":detail}),
                )?;
                // Session IPC keeps a bounded recent event history during long play.
                // The owning supervisor also consumes every event from stdout.
                if self.bytes.saturating_add(line.len() + 1) > 1024 * 1024 {
                    self.file.set_len(0)?;
                    self.file.seek(SeekFrom::Start(0))?;
                    self.bytes = 0;
                }
                writeln!(self.file, "{line}")?;
                self.bytes += line.len() + 1;
                self.file.flush()?;
                println!("{line}");
                Ok(())
            })(),
            Code::OutputLog,
        )
    }
}

/// Pure authorization check before any frame reaches the protocol state machine.
fn allowed_peer(src: Mac, expected: Mac) -> bool {
    src == expected
}

// A normal owner/heartbeat shutdown closes the worker's channel while the main
// loop may already be waiting in recv_timeout. Preserve real worker failures,
// but do not turn that expected close into a transport failure.
fn receive_closed(shutdown_requested: bool, failure: Option<Code>) -> Result<()> {
    if let Some(code) = failure {
        anyhow::bail!(code);
    }
    ensure!(shutdown_requested, Code::RxThreadClosed);
    Ok(())
}

// Retried Ozmo batches include the unacknowledged window. Preserve unnumbered
// handshake replies while dropping stale retries when a native send stalls.
fn latest_batch(
    mut batch: Vec<(Mac, Vec<u8>)>,
    rx: &mpsc::Receiver<Vec<(Mac, Vec<u8>)>>,
) -> Vec<(Mac, Vec<u8>)> {
    let mut once = Vec::new();
    for _ in 0..64 {
        let Ok(newer) = rx.try_recv() else { break };
        once.extend(
            batch
                .into_iter()
                .filter(|(_, frame)| frame.len() < 6 || frame[2..6] == [0; 4]),
        );
        batch = newer;
    }
    once.extend(batch);
    once
}

fn main() {
    std::panic::set_hook(Box::new(|_| eprintln!("{{\"stage\":\"panic_redacted\"}}")));
    let args = Args::parse();
    if !args.run {
        println!(
            "{}",
            json!({"stage":"plan","hardware_opened":false,"required_channel":36,"gamepad_only":false,"touchpad_mouse":true,"desktop_buttons":true,"starts_or_stops_ap":false,"requires_installed_private_config":true,"lifetime":"owner process and heartbeat","owner_heartbeat":"alive\\n every <=1s; loss >=3s stops child"})
        );
        return;
    }
    let result = run(args);
    if let Err(error) = result {
        eprintln!(
            "{}",
            json!({"stage":"failed_redacted","error_code":diagnostic::category(&error)})
        );
        std::process::exit(2);
    }
}

fn run(args: Args) -> Result<()> {
    let mut log = checkpoint(
        Log::new(
            args.output
                .as_deref()
                .ok_or_else(|| anyhow::anyhow!("missing_output"))?,
        ),
        Code::OutputLog,
    )?;
    let begun = Instant::now();
    let quit = Arc::new(AtomicBool::new(false));
    let heartbeat = Arc::new(AtomicU64::new(0));
    let finished = Arc::new(AtomicBool::new(false));
    let watchdog_quit = quit.clone();
    let watchdog_beat = heartbeat.clone();
    let watchdog_done = finished.clone();
    std::thread::spawn(move || {
        let mut stopping = None;
        while !watchdog_done.load(Ordering::Relaxed) {
            let ms = begun.elapsed().as_millis() as u64;
            if ms.saturating_sub(watchdog_beat.load(Ordering::Relaxed)) > 3000
                || watchdog_quit.load(Ordering::Relaxed)
            {
                watchdog_quit.store(true, Ordering::Relaxed);
                let at = stopping.get_or_insert(Instant::now());
                if at.elapsed() >= Duration::from_secs(3) {
                    eprintln!("{{\"stage\":\"watchdog_exit_cleanup_unconfirmed\"}}");
                    std::process::exit(89);
                }
            }
            std::thread::sleep(Duration::from_millis(50));
        }
    });
    let beat_quit = quit.clone();
    let beat = heartbeat.clone();
    std::thread::spawn(move || {
        let stdin = std::io::stdin();
        let mut input = stdin.lock();
        loop {
            let mut token = [0u8; 6];
            if input.read_exact(&mut token).is_err() || token != *b"alive\n" {
                beat_quit.store(true, Ordering::Relaxed);
                break;
            }
            beat.store(begun.elapsed().as_millis() as u64, Ordering::Relaxed);
            if beat_quit.load(Ordering::Relaxed) {
                break;
            }
        }
    });
    let result = (|| {
        let r = checkpoint(
            lease::OwnerReceipt::read(
                args.owner_receipt
                    .as_deref()
                    .ok_or_else(|| anyhow::anyhow!("missing_owner"))?,
            ),
            Code::OwnerReceipt,
        )?;
        let peer = r.controller_mac;
        let output = lease::in_root(args.output.as_deref().unwrap())?;
        ensure!(
            output.starts_with(lease::root()?.join("sessions").join(&r.session_id)),
            Code::OutputLog
        );
        let owner = checkpoint(platform::Owner::open(&r), Code::OwnerProcess)?;
        platform::verify_adapter(&r)?;
        ensure!(!quit.load(Ordering::Relaxed), Code::OwnerLeaseEnded);
        log.event("owner-and-config-validated",json!({"channel":r.channel,"exact_adapter":true,"expected_controller_identity":true,"private_config_verified":true}))?;
        checkpoint(npcap::init_dll_path(), Code::NpcapInit)?;
        data_loop(&r, peer, &owner, begun, &quit, &mut log)
    })();
    quit.store(true, Ordering::Relaxed);
    let final_log=checkpoint(log.event("finished",json!({"ok":result.is_ok(),"error_code":result.as_ref().err().map(diagnostic::category),"ap_mutation_calls":0,"global_input_calls":0,"credentials_logged":false,"raw_packet_logs":false,"physical_fox_movement_verified":false})), Code::OutputLog);
    finished.store(true, Ordering::Relaxed);
    result.and(final_log)
}

fn data_loop(
    r: &lease::OwnerReceipt,
    peer: Mac,
    owner: &platform::Owner,
    begun: Instant,
    quit: &Arc<AtomicBool>,
    log: &mut Log,
) -> Result<()> {
    let mut tx_link = checkpoint(
        npcap::Link::open_tx(&r.adapter_guid, r.adapter_mac),
        Code::NpcapOpenTx,
    )?;
    let mut rx_link = checkpoint(
        npcap::Link::open_rx(&r.adapter_guid, r.adapter_mac, peer),
        Code::NpcapOpenRx,
    )?;
    let (out_tx, out_rx) = mpsc::sync_channel::<Vec<(Mac, Vec<u8>)>>(64);
    let (in_tx, in_rx) = mpsc::sync_channel::<(Mac, Vec<u8>)>(256);
    let failed = Arc::new(AtomicBool::new(false));
    let failure_code = Arc::new(AtomicU8::new(0));
    let tx_code = failure_code.clone();
    let tx_quit = quit.clone();
    let tx_failed = failed.clone();
    let tx_thread = std::thread::spawn(move || {
        while !tx_quit.load(Ordering::Relaxed) {
            match out_rx.recv_timeout(Duration::from_millis(10)) {
                Ok(batch) => {
                    for (dst, data) in latest_batch(batch, &out_rx) {
                        if tx_quit.load(Ordering::Relaxed) {
                            return;
                        }
                        if !allowed_peer(dst, peer) {
                            diagnostic::record_first(&tx_code, Code::TxUnexpectedPeer);
                            tx_failed.store(true, Ordering::Release);
                            return;
                        }
                        if tx_link.send(dst, &data).is_err() {
                            diagnostic::record_first(&tx_code, Code::TxSend);
                            tx_failed.store(true, Ordering::Release);
                            return;
                        }
                    }
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(_) => return,
            }
        }
    });
    let rx_quit = quit.clone();
    let rx_failed = failed.clone();
    let rx_code = failure_code.clone();
    let rx_thread = std::thread::spawn(move || {
        while !rx_quit.load(Ordering::Relaxed) {
            match rx_link.recv() {
                Ok(Some((src, data, _))) if allowed_peer(src, peer) => {
                    if in_tx.try_send((src, data)).is_err() {
                        diagnostic::record_first(&rx_code, Code::RxQueueFull);
                        rx_failed.store(true, Ordering::Release);
                        return;
                    }
                }
                Ok(_) => {}
                Err(_) => {
                    diagnostic::record_first(&rx_code, Code::RxRead);
                    rx_failed.store(true, Ordering::Release);
                    return;
                }
            }
        }
    });
    let mut output = output::Output::new();
    let mut host = Host::new(Config::default());
    let mut ctl = controller::Controller::new(false);
    let mut last_rx = None;
    let mut last_verify = Instant::now();
    let mut last_slow = Instant::now();
    let (mut frames, mut pad_reports, mut trackpad_reports) = (0u64, 0u64, 0u64);
    let result = (|| {
        log.event(
            "transport-ready",
            json!({"expected_peer_filter":true,"mic_opened":false,"rumble_forwarded":false}),
        )?;
        while !quit.load(Ordering::Relaxed) {
            ensure!(
                !failed.load(Ordering::Acquire),
                diagnostic::transport_code(&failure_code)
            );
            let now = Instant::now();
            if checkpoint(output.flush(), Code::OutputPad)? && !output.ready_reported {
                log.event(
                    "xinput-ready",
                    json!({"slot":output.slot,"actual_pad_report":true}),
                )?;
                output.ready_reported = true;
            }
            if last_verify.elapsed() >= Duration::from_secs(1) {
                ensure!(owner.alive(), Code::OwnerExited);
                platform::verify_adapter(r)?;
                log.event("observing",json!({"received_frames":frames,"pad_reports":pad_reports,"trackpad_reports":trackpad_reports,"xinput_slot":output.slot}))?;
                last_verify = now;
            }
            match in_rx.recv_timeout(Duration::from_millis(10)) {
                Ok((src, data)) => {
                    if !allowed_peer(src, peer) {
                        continue;
                    }
                    frames += 1;
                    last_rx = Some(now);
                    for event in host.on_frame(now, src, &data) {
                        handle_event(
                            event,
                            now,
                            &mut host,
                            &mut ctl,
                            &mut output,
                            log,
                            &mut pad_reports,
                            &mut trackpad_reports,
                        )?;
                    }
                }
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => {
                    receive_closed(
                        quit.load(Ordering::Relaxed),
                        failed
                            .load(Ordering::Acquire)
                            .then(|| diagnostic::transport_code(&failure_code)),
                    )?;
                    break;
                }
            }
            if last_slow.elapsed() >= Duration::from_millis(100) {
                for event in host.poll(now) {
                    handle_event(
                        event,
                        now,
                        &mut host,
                        &mut ctl,
                        &mut output,
                        log,
                        &mut pad_reports,
                        &mut trackpad_reports,
                    )?;
                }
                if host.peer().is_some() {
                    ctl.poll(now, &mut host);
                }
                if last_rx.is_some_and(|t| now.duration_since(t) > Duration::from_secs(1)) {
                    checkpoint(output.release(), Code::OutputRelease)?;
                    last_rx = None;
                    log.event("stale-input-released", json!({}))?;
                }
                last_slow = now;
            }
            output.tick(Instant::now())?;
            let frames = host.take_tx();
            if !frames.is_empty() {
                ensure!(
                    frames.iter().all(|(dst, _)| allowed_peer(*dst, peer)),
                    Code::TxUnexpectedOutput
                );
                out_tx
                    .try_send(frames)
                    .map_err(|_| anyhow::anyhow!(Code::TxQueueFull))?;
            }
        }
        log.event("session-ended",json!({"elapsed_ms":begun.elapsed().as_millis(),"pad_reports":pad_reports,"wireless_input_observed":pad_reports>0}))?;
        Ok(())
    })();
    quit.store(true, Ordering::Relaxed);
    let close = checkpoint(output.close(), Code::OutputClose);
    drop(out_tx);
    let tx_join = tx_thread.join();
    let rx_join = rx_thread.join();
    log.event("data-host-released",json!({"virtual_pad_removed":close.is_ok(),"transport_threads_joined":tx_join.is_ok()&&rx_join.is_ok(),"ap_left_to_owner":true}))?;
    ensure!(
        tx_join.is_ok() && rx_join.is_ok(),
        Code::TransportThreadPanicked
    );
    result.and(close)
}

#[allow(clippy::too_many_arguments)]
fn handle_event(
    event: Event,
    now: Instant,
    host: &mut Host,
    ctl: &mut controller::Controller,
    output: &mut output::Output,
    log: &mut Log,
    pad_reports: &mut u64,
    trackpad_reports: &mut u64,
) -> Result<()> {
    match event {
        Event::Connected { .. } => {
            checkpoint(output.release(), Code::OutputRelease)?;
            ctl.start(now, host);
            log.event("expected-controller-session", json!({}))?;
        }
        Event::Usb(message) => {
            for report in ctl.on_usb(now, &message, host) {
                match report {
                    controller::Report::Pad(p) if ctl.ready() => {
                        if output.slot.is_none() {
                            checkpoint(output.plug(), Code::OutputPlug)?;
                        }
                        let delivered = match output.pad(&p) {
                            Ok(delivered) => delivered,
                            Err(error) => {
                                log.event(
                                    "gamepad-output-failed",
                                    json!({"driver_error":error.downcast_ref::<vigem_client::Error>().map(|e| format!("{e:?}"))}),
                                )?;
                                return Err(error.context(Code::OutputPad));
                            }
                        };
                        if delivered && !output.ready_reported {
                            log.event(
                                "xinput-ready",
                                json!({"slot":output.slot,"actual_pad_report":true}),
                            )?;
                            output.ready_reported = true;
                        }
                        *pad_reports += 1;
                    }
                    controller::Report::Trackpad(t) if ctl.ready() => {
                        output.mouse(&t)?;
                        *trackpad_reports += 1;
                    }
                    _ => {}
                }
            }
        }
        Event::Disconnect | Event::Stopped => {
            checkpoint(output.release(), Code::OutputRelease)?;
            log.event("controller-session-released", json!({}))?;
        }
        Event::Rejected { .. } => anyhow::bail!(Code::OzmoConnectRejected),
        _ => {}
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn requested_shutdown_can_close_receiver_without_failure() {
        assert!(receive_closed(true, None).is_ok());
        assert_eq!(
            diagnostic::category(&receive_closed(false, None).unwrap_err()),
            "rx_thread_closed"
        );
        assert_eq!(
            diagnostic::category(&receive_closed(true, Some(Code::RxRead)).unwrap_err()),
            "rx_read"
        );
    }
    #[test]
    fn runtime_is_owner_scoped_not_diagnostic_duration() {
        assert!(!Args::try_parse_from(["host"]).unwrap().run);
        assert!(Args::try_parse_from(["host", "--seconds", "900"]).is_err());
        assert!(Args::try_parse_from(["host", "--owner-receipt", "unused"]).is_err());
    }
    #[test]
    fn protocol_rejects_other_peers_before_session() {
        assert!(allowed_peer([2; 6], [2; 6]));
        assert!(!allowed_peer([4; 6], [2; 6]));
    }
    #[test]
    fn coalescing_preserves_handshake_but_drops_old_retries() {
        let (tx, rx) = mpsc::sync_channel(64);
        let once = ([2; 6], vec![1, 2, 0, 0, 0, 0]);
        let old = ([2; 6], vec![1, 2, 1, 0, 0, 0]);
        let new = ([2; 6], vec![1, 2, 2, 0, 0, 0]);
        tx.send(vec![new.clone()]).unwrap();
        assert_eq!(latest_batch(vec![once.clone(), old], &rx), vec![once, new]);
    }
}
