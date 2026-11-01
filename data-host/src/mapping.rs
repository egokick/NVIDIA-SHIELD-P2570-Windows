use crate::controller::{Pad, btn, key};
use vigem_client::{XButtons, XGamepad};
pub fn to_xgamepad(p: &Pad) -> XGamepad {
    use vigem_client::XButtons as X;
    let mut raw = 0u16;
    for (from, to) in [
        (btn::A, X::A),
        (btn::B, X::B),
        (btn::X, X::X),
        (btn::Y, X::Y),
        (btn::LB, X::LB),
        (btn::RB, X::RB),
        (btn::L3, X::LTHUMB),
        (btn::R3, X::RTHUMB),
        (btn::START, X::START),
    ] {
        if p.buttons & from != 0 {
            raw |= to;
        }
    }
    if p.keys & key::BACK != 0 {
        raw |= X::BACK;
    }
    // Home is Guide. The NVIDIA button stays the controller's own, for connecting and power.
    if p.keys & key::HOME != 0 {
        raw |= X::GUIDE;
    }
    raw |= match p.hat {
        0 => X::UP,
        1 => X::UP | X::RIGHT,
        2 => X::RIGHT,
        3 => X::DOWN | X::RIGHT,
        4 => X::DOWN,
        5 => X::DOWN | X::LEFT,
        6 => X::LEFT,
        7 => X::UP | X::LEFT,
        _ => 0,
    };
    XGamepad {
        buttons: XButtons { raw },
        left_trigger: (p.lt >> 8) as u8,
        right_trigger: (p.rt >> 8) as u8,
        thumb_lx: axis(p.lx, false),
        // HID Y grows downward, XInput Y grows upward
        thumb_ly: axis(p.ly, true),
        thumb_rx: axis(p.rx, false),
        thumb_ry: axis(p.ry, true),
    }
}

fn axis(v: u16, invert: bool) -> i16 {
    let c = i32::from(v) - 0x8000;
    let c = if invert { -c - 1 } else { c };
    c.clamp(i32::from(i16::MIN), i32::from(i16::MAX)) as i16
}
