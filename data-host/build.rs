fn main() {
    let sdk = std::env::var("NPCAP_SDK_DIR").expect("NPCAP_SDK_DIR required");
    println!("cargo:rustc-link-search=native={sdk}/Lib/x64");
    println!("cargo:rustc-link-arg=/DELAYLOAD:wpcap.dll");
    println!("cargo:rustc-link-lib=delayimp");
}
