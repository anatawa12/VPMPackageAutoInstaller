use std::collections::HashMap;
use std::fmt::Formatter;
use std::io;
use indexmap::IndexMap;
use reqwest::{Client, Url};
use serde::{Deserialize, Deserializer};
use serde::de::{Error, MapAccess, Visitor};
use serde::de::value::{MapAccessDeserializer, StrDeserializer};
use crate::functions::{display_dialog, log_error};
use crate::interlop::NativeCsData;
use crate::version::VersionRange;
use crate::vpm::{AddPackageErr, Environment, UnityProject, VersionSelector};

// vpai uses some parts of vrc-get so allow dead code
#[allow(dead_code)]
mod vpm;
mod version;

const CURRENT_VERSION: u64 = 1;

mod interlop {
    use std::panic::catch_unwind;
    use crate::{CURRENT_VERSION, vpai_native_impl};
    use crate::functions::{display_dialog, NativeDataScope};

    #[repr(C)]
    pub(crate) struct NativeCsData {
        pub(crate) version: u64,
        version_mismatch: extern "system" fn () -> (),
        // end of version independent part

        // config json info. might not be utf8
        pub(crate) config_ptr: *const u8,
        pub(crate) config_len: usize,

        // vtable
        pub(crate) display_dialog: extern "system" fn (
            usize, usize, // title
            usize, usize, // message
            usize, usize, // ok
            usize, usize, // cancel
        ) -> bool,
        pub(crate) log_error: extern "system" fn (
            usize, usize, // message
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
    }

    #[no_mangle]
    #[allow(dead_code)] // will be called from C#
    extern "system" fn vpai_native_entry_point(data: &NativeCsData) -> bool {
        if data.version != CURRENT_VERSION {
            data.version_mismatch();
            return false;
        }

        let _ = NativeDataScope::new(data);

        match catch_unwind(move || {
            let panic = match catch_unwind(|| vpai_native_impl(data)) {
                Ok(r) => return r,
                Err(panic) => panic,
            };

            let message = match panic.downcast_ref::<&'static str>() {
                Some(s) => *s,
                None => match panic.downcast_ref::<String>() {
                    Some(s) => &s[..],
                    None => "Box<dyn Any>",
                },
            };

            display_dialog(
                "ERROR",
                &format!("Unexpected error: {}", message),
                "OK",
                "",
            );
            false
        }) {
            Ok(r) => return r,
            Err(_second_panic) => (),
        }

        display_dialog(
            "ERROR",
            "unrecoverable error in VPAI Installer",
            "OK",
            "",
        );

        false
    }
}

mod functions {
    use std::cell::UnsafeCell;
    use std::ptr::null;
    use crate::interlop::NativeCsData;

    thread_local! {
        static NATIVE_DATA: UnsafeCell<*const NativeCsData> = UnsafeCell::new(null());
    }

    pub(crate)  struct NativeDataScope(());

    impl NativeDataScope {
        pub fn new(data: &NativeCsData) -> NativeDataScope {
            NATIVE_DATA.with(|x| unsafe { *x.get() = data });
            NativeDataScope(())
        }
    }

    impl Drop for NativeDataScope {
        fn drop(&mut self) {
            NATIVE_DATA.with(|x| unsafe { *x.get() = null() });
        }
    }

    fn native_data() -> &'static NativeCsData {
        unsafe {
            let ptr = NATIVE_DATA.with(|x| unsafe { *x.get() });
            assert_ne!(ptr, null(), "NATIVE DATA NOT SET");
            &*ptr
        }
    }

    pub fn display_dialog(title: &str, message: &str, ok: &str, cancel: &str) -> bool {
        (native_data().display_dialog)(
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

    pub fn log_error(message: &str) {
        (native_data().log_error)(message.as_ptr() as usize, message.len());
    }
}

fn vpai_native_impl(data: &NativeCsData) -> bool {
    match vpai_native_impl_async(data) {
        Ok(r) => r,
        Err((e, context)) => {
            log_error(&format!("Error: {}, {}", context, e));
            display_dialog(
                "ERROR", &format!("Error installing packages: {}, {}", context, e),
                "ok", "");
            false
        }
    }
}

#[tokio::main(flavor = "current_thread")]
async fn vpai_native_impl_async(data: &NativeCsData) -> Result<bool, (io::Error, &'static str)> {
    let Some(config) = std::str::from_utf8(data.config_bytes()).ok()
        .and_then(|x| serde_json::from_str::<VpaiConfig>(x).ok()) else {
        display_dialog("ERROR", "invalid config.json", "OK", "");
        return Ok(false);
    };
    let client = Client::builder()
            .user_agent(concat!(
            "VpmPackageAutoInstaller/0.3 (github:anatawa12/VpmPackageAutoInstaller) ",
            "vrc-get/", env!("CARGO_PKG_VERSION"), 
            " (github:anatawa12/vrc-get; VPAI is based on vrc-get but modified)"))
            .build()
            .expect("building client");
    let mut env = Environment::load_default(Some(client)).await.context("loading env")?;
    let cwd = std::env::current_dir().context("getting cwd")?;
    let mut unity_project = UnityProject::find_unity_project(Some(cwd)).await.context("loading unity project")?;
    env.load_package_infos().await.context("Downloading package information")?;

    for repo in config.vpm_repositories {
        env.add_pending_repository(repo.url, repo.headers)
            .await.context("loading repositories")?;
    }

    let mut dependencies = Vec::with_capacity(config.vpm_dependencies.len());
    for (pkg, range) in config.vpm_dependencies {
        let Some(found) = env.find_package_by_name(&pkg, VersionSelector::Range(&range)) else {
            display_dialog("ERROR", &format!("Package not found: {} version {}", pkg, range), "OK", "");
            return Ok(false)
        };
        dependencies.push(found)
    }

    let request = match unity_project.add_package_request(&env, dependencies, true).await {
        Ok(request) => request,
        Err(AddPackageErr::Io(e)) => return Err((e, "finding legacy assets")),
        Err(e) => {
            display_dialog("ERROR", &e.to_string(), "OK", "");
            return Ok(false)
        },
    };

    if request.locked().len() == 0 {
        display_dialog("Nothing TO DO!", "All Packages are Installed!", "OK", "");
        return Ok(false);
    }

    // always compute prompt
    // TODO: check for no prompt
    if true {
        let mut confirm_message = "You're installing the following packages:".to_string();

        for (name, version) in request.locked().iter().map(|x| (x.name(), x.version()))
            .chain(request.dependencies().iter().map(|(name, dep)| (*name, &dep.version))) {
            confirm_message.push('\n');
            confirm_message.push_str(name);
            confirm_message.push_str(" version ");
            confirm_message.push_str(&version.to_string());
        }

        if env.pending_repositories().len() != 0 {
            confirm_message.push_str("\n\nThis will add following repositories:");
            for (_, url) in env.pending_repositories() {
                // ReSharper disable once PossibleNullReferenceException
                confirm_message.push('\n');
                confirm_message.push_str(url.as_str());
            }
        }

        if request.legacy_folders().len() != 0 || request.legacy_files().len() != 0
        {
            confirm_message.push_str("\n\nYou're also deleting the following files/folders:");
            for path in request.legacy_folders().iter().chain(request.legacy_files()) {
                confirm_message.push('\n');
                confirm_message.push_str(&path.to_string_lossy());
            }
        }

        if display_dialog("Confirm", &confirm_message, "Install", "Cancel") {
            return Ok(false);
        }
    }

    unity_project.do_add_package_request(&env, request).await.context("adding package request")?;

    env.save_pending_repositories().await.context("installing repositories")?;

    unity_project.save().await.context("save unity package")?;
    env.save().await.context("save global config")?;

    Ok(true)
}

trait Context<T> {
    fn context(self, ctx: &'static str) -> Result<T, (io::Error, &'static str)>;
}

impl <T> Context<T> for Result<T, io::Error> {
    fn context(self, ctx: &'static str) -> Result<T, (io::Error, &'static str)> {
        self.map_err(|e| (e, ctx))
    }
}

#[derive(Deserialize)]
struct VpaiConfig {
    #[serde(default, rename="vpmRepositories")]
    vpm_repositories: Vec<VpaiRepository>,
    #[serde(default, rename="includePrerelease")]
    #[allow(dead_code)] // TODO: support allow
    include_prerelease: bool,
    #[serde(default, rename="vpmDependencies")]
    vpm_dependencies: HashMap<String, VersionRange>,
}

struct VpaiRepository {
    url: Url,
    headers: IndexMap<String, String>,
}

impl<'de> Deserialize<'de> for VpaiRepository {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error> where D: Deserializer<'de> {
        #[derive(Deserialize)]
        struct Struct {
            url: Url,
            headers: IndexMap<String, String>,
        }
        struct VisitorImpl;
        impl<'de> Visitor<'de> for VisitorImpl {
            type Value = Struct;

            fn expecting(&self, formatter: &mut Formatter) -> std::fmt::Result {
                formatter.write_str("string or map")
            }

            fn visit_str<E>(self, v: &str) -> Result<Self::Value, E> where E: Error {
                Ok(Struct {
                    url: Deserialize::deserialize(StrDeserializer::new(v))?,
                    headers: IndexMap::new(),
                })
            }

            fn visit_map<A>(self, map: A) -> Result<Self::Value, A::Error> where A: MapAccess<'de> {
                Deserialize::deserialize(MapAccessDeserializer::new(map))
            }
        }

        let Struct { url, headers } = deserializer.deserialize_any(VisitorImpl)?;
        Ok(VpaiRepository { url, headers })
    }
}
