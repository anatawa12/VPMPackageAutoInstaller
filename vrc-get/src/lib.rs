use std::collections::HashMap;
use std::fmt::Formatter;
use std::io;
use reqwest::Client;
use serde::{Deserialize, Deserializer};
use serde::de::{Error, MapAccess, Visitor};
use crate::interlop::NativeCsData;
use crate::version::VersionRange;

mod vpm;
mod version;

const CURRENT_VERSION: u64 = 1;

mod interlop {
    use std::panic::catch_unwind;
    use crate::{CURRENT_VERSION, vpai_native_impl};

    #[repr(C)]
    pub(crate) struct NativeCsData {
        pub(crate) version: u64,
        version_mismatch: extern "system" fn () -> (),
        // end of version independent part

        // config json info. might not be utf8
        config_ptr: *const u8,
        config_len: usize,

        // vtable
        display_dialog: extern "system" fn (
            usize, usize, // title
            usize, usize, // message
            usize, usize, // ok
            usize, usize, // cancel
        ) -> bool,
    }

    impl NativeCsData {
        pub(crate) fn version_mismatch(&self) {
            (self.version_mismatch)()
        }

        pub(crate) fn config_bytes(&self) -> &[u8] {
            unsafe {
                std::slice::from_raw_parts(
                    self.config_ptr,
                    self.config_len,
                )
            }
        }

        pub fn display_dialog(&self, title: &str, message: &str, ok: &str, cancel: &str) -> bool {
            (self.display_dialog)(
                title.as_ptr() as usize,
                title.len(),
                message.as_ptr() as usize,
                message.len(),
                ok.as_ptr() as usize,
                ok.len(),
                cancel.as_ptr() as usize,
                cancel.len(),
            )
        }
    }

    #[no_mangle]
    #[allow(dead_code)] // will be called from C#
    extern "system" fn vpai_native_entry_point(data: &NativeCsData) {
        if data.version != CURRENT_VERSION {
            data.version_mismatch();
            return;
        }

        let Err(_second_panic) = catch_unwind(move || {
            let Err(panic) = catch_unwind(|| vpai_native_impl(data)) else {
                return; // good. successful or recoverable error exit
            };

            let message = match panic.downcast_ref::<&'static str>() {
                Some(s) => *s,
                None => match panic.downcast_ref::<String>() {
                    Some(s) => &s[..],
                    None => "Box<dyn Any>",
                },
            };

            data.display_dialog(
                "ERROR",
                &format!("Unexpected error: {}", message),
                "OK",
                "",
            );
        }) else { return };


        data.display_dialog(
            "ERROR",
            "unrecoverable error in VPAI Installer",
            "OK",
            "",
        );
    }
}


async fn vpai_native_impl(data: &NativeCsData) {
    vpai_native_impl_async(data);
}

#[tokio::main(flavor = "current_thread")]
async fn vpai_native_impl_async(data: &NativeCsData) -> io::Result<()> {
    let Some(config) = std::str::from_utf8(data.config_bytes()).ok()
        .and_then(|x| serde_json::from_str::<VpaiConfig>(x).ok()) else {
        data.display_dialog("ERROR", "invalid config.json", "OK", "");
        return Ok(());
    };
    let client = Client::builder()
            .user_agent(concat!(
            "VpmPackageAutoInstaller/0.3 (github:anatawa12/VpmPackageAutoInstaller) ",
            "vrc-get/", env!("CARGO_PKG_VERSION"), 
            " (github:anatawa12/vrc-get; VPAI is based on vrc-get but modified)"))
            .build()
            .expect("building client");
    todo!();
    Ok(())
}

#[derive(Deserialize)]
struct VpaiConfig {
    #[serde(default, rename="vpmRepositories")]
    vpm_repositories: Vec<VpaiRepository>,
    #[serde(default, rename="includePrerelease")]
    include_prerelease: bool,
    #[serde(default, rename="vpmDependencies")]
    vpm_dependencies: HashMap<String, VersionRange>,
}

struct VpaiRepository {
    url: String,
    headers: HashMap<String, String>,
}

impl<'de> Deserialize<'de> for VpaiRepository {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error> where D: Deserializer<'de> {
        #[derive(Deserialize)]
        struct Struct {
            url: String,
            headers: HashMap<String, String>,
        }
        struct VisitorImpl;
        impl<'de> Visitor<'de> for VisitorImpl {
            type Value = Struct;

            fn expecting(&self, formatter: &mut Formatter) -> std::fmt::Result {
                formatter.write_str("string or map")
            }

            fn visit_str<E>(self, v: &str) -> Result<Self::Value, E> where E: Error {
                self.visit_string(v.to_owned())
            }

            fn visit_string<E>(self, url: String) -> Result<Self::Value, E> where E: Error {
                Ok(Struct { url, headers: HashMap::new() })
            }

            fn visit_map<A>(self, map: A) -> Result<Self::Value, A::Error> where A: MapAccess<'de> {
                Deserialize::deserialize(serde::de::value::MapAccessDeserializer::new(map))
            }
        }

        let Struct { url, headers } = deserializer.deserialize_any(VisitorImpl)?;
        Ok(VpaiRepository { url, headers })
    }
}
