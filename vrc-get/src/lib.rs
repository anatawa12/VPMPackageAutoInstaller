use std::collections::HashMap;
use std::fmt::Formatter;
use std::io;
use indexmap::IndexMap;
use serde::{Deserialize, Deserializer};
use serde::de::{Error, MapAccess, Visitor};
use serde::de::value::{MapAccessDeserializer, StrDeserializer};
use crate::interlop::{display_dialog, log_error, prompt_enabled};
use crate::interlop::NativeCsData;
use crate::version::VersionRange;
use crate::vpm::{AddPackageErr, Environment, UnityProject, VersionSelector};

// vpai uses some parts of vrc-get so allow dead code
#[allow(dead_code)]
mod vpm;
mod version;
mod reqwest_cs;
use reqwest_cs as reqwest;
use reqwest::Url;

const CURRENT_VERSION: u64 = 1;

mod interlop {
    use std::panic::catch_unwind;
    use crate::{CURRENT_VERSION, vpai_native_impl};
    use std::cell::UnsafeCell;
    use std::marker::PhantomData;
    use std::ops::Deref;
    use std::ptr::null;

    // C# owned value
    #[repr(transparent)]
    pub struct CsHandle(usize);

    impl CsHandle {
        pub fn invalid() -> Self {
            Self(0)
        }

        pub fn is_invalid(&self) -> bool {
            self.0 == 0
        }

        pub fn as_ref(&self) -> CsHandleRef {
            CsHandleRef(self.0, PhantomData)
        }

        pub fn take(&mut self) -> CsHandle {
            std::mem::replace(self, Self::invalid())
        }
    }

    impl Drop for CsHandle {
        fn drop(&mut self) {
            (native_data().free_cs_memory)(self.0)
        }
    }

    #[repr(transparent)]
    #[derive(Copy, Clone)]
    pub struct CsHandleRef<'a>(usize, PhantomData<&'a ()>);

    // C# owned slice
    #[repr(C)]
    pub struct CsSlice<T> {
        handle: CsHandle,
        ptr: usize,
        len: usize,
        _phantom: PhantomData<*const T>,
    }

    impl <T> CsSlice<T> {
        pub fn invalid() -> Self {
            Self {
                handle: CsHandle::invalid(),
                ptr: 0,
                len: 0,
                _phantom: PhantomData
            }
        }

        pub fn is_invalid(&self) -> bool {
            self.handle.is_invalid()
        }

        pub fn take(&mut self) -> CsSlice<T> {
            std::mem::replace(self, Self::invalid())
        }

        pub fn as_slice(&self) -> &[T] {
            // allow nullptr for empty ones
            if self.len == 0 {
                return &[];
            }
            unsafe {
                std::slice::from_raw_parts(self.ptr as *const T, self.len)
            }
        }
    }

    impl <T> Deref for CsSlice<T> {
        type Target = [T];

        fn deref(&self) -> &Self::Target {
            self.as_slice()
        }
    }

    impl <T> AsRef<[T]> for CsSlice<T> {
        fn as_ref(&self) -> &[T] {
            self.as_slice()
        }
    }

    // C# owned string
    #[repr(transparent)]
    pub(crate) struct CsStr {
        slice: CsSlice<u8>,
    }

    impl CsStr {
        pub fn invalid() -> Self {
            Self { slice: CsSlice::invalid() }
        }

        pub fn is_invalid(&self) -> bool {
            self.slice.is_invalid()
        }

        pub fn as_str(&self) -> &str {
            unsafe { std::str::from_utf8_unchecked(self.slice.as_slice()) }
        }
    }

    impl Deref for CsStr {
        type Target = str;

        fn deref(&self) -> &Self::Target {
            self.as_str()
        }
    }

    impl AsRef<str> for CsStr {
        fn as_ref(&self) -> &str {
            self.as_str()
        }
    }

    #[derive(Copy, Clone)]
    #[repr(C)]
    pub(crate) struct RsSlice<T> {
        ptr: *const T,
        len: usize,
    }

    impl <T> RsSlice<T> {
        pub(crate) fn new(str: &[T]) -> Self {
            Self {
                ptr: str.as_ptr(),
                len: str.len(),
            }
        }
    }

    #[repr(transparent)]
    #[derive(Copy, Clone)]
    pub(crate) struct RsStr {
        _slice: RsSlice<u8>,
    }

    impl RsStr {
        pub(crate) fn new(str: &str) -> Self {
            Self { _slice: RsSlice::new(str.as_bytes()) }
        }
    }

    #[repr(C)]
    pub(crate) struct CsErr {
        pub str: CsStr,
        pub as_id: i32,
    }

    impl CsErr {
        pub fn invalid() -> Self {
            Self {
                str: CsStr::invalid(),
                as_id: 0,
            }
        }

        pub fn is_invalid(&self) -> bool {
            self.str.is_invalid()
        }
    }

    #[repr(C)]
    pub(crate) struct NativeCsData {
        pub(crate) version: u64,
        version_mismatch: extern "system" fn () -> (),
        // end of version independent part

        // config json info. might not be utf8
        pub(crate) config_ptr: *const u8,
        pub(crate) config_len: usize,

        // vtable
        pub(crate) prompt_enabled: extern "system" fn () -> bool,
        pub(crate) display_dialog: extern "system" fn (title: &RsStr, message: &RsStr, ok: &RsStr, cancel: &RsStr) -> bool,
        pub(crate) log_error: extern "system" fn (message: &RsStr),
        pub(crate) guid_to_asset_path: extern "system" fn (
            &[u8; 128/8], // guid
            &mut [usize; 2], // result
        ),
        // memory util (for rust memory)
        pub(crate) free_cs_memory: extern "system" fn (handle: usize),
        pub(crate) verify_url: extern "system" fn (version: &RsStr) -> bool,
        // http client
        pub(crate) web_client_new: extern "system" fn (version: &RsStr) -> CsHandle,
        pub(crate) web_request_new_get: extern "system" fn (this: CsHandleRef, url: &RsStr) -> CsHandle,
        pub(crate) web_request_add_header: extern "system" fn (this: CsHandleRef, name: &RsStr, value: &RsStr, err: &mut CsErr),
        // important: not ref: rust throw away the ownership
        pub(crate) web_request_send: extern "system" fn (this: CsHandle, result: &mut CsHandle, err: &mut CsErr, context: *const (), callback: fn(*const ()) -> ()),
        pub(crate) web_response_status: extern "system" fn (this: CsHandleRef) -> u32,
        pub(crate) web_response_headers: extern "system" fn (this: CsHandleRef) -> CsHandle,
        // important: not ref: rust throw away the ownership
        pub(crate) web_response_async_reader: extern "system" fn (this: CsHandle) -> CsHandle,
        // important: not ref: rust throw away the ownership
        pub(crate) web_response_bytes_async: extern "system" fn (this: CsHandle, slice: &mut CsSlice<u8>, err: &mut CsErr, context: *const (), callback: fn(*const ()) -> ()),
        pub(crate) web_headers_get: extern "system" fn (this: CsHandleRef, name: &RsStr, slice: &mut CsStr),
        pub(crate) web_async_reader_read: extern "system" fn (this: CsHandleRef, slice: &mut CsSlice<u8>, err: &mut CsErr, context: *const (), callback: fn(*const ()) -> ()),
        // others
        pub(crate) async_unzip: extern "system" fn (file_handle: isize, dest_dir: &RsStr, err: &mut CsErr, context: *const (), callback: fn(*const ()) -> ()),
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

        let scope = NativeDataScope::new(data);

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

        drop(scope);

        false
    }

    thread_local! {
        static NATIVE_DATA: UnsafeCell<*const NativeCsData> = UnsafeCell::new(null());
    }

    struct NativeDataScope(());

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

    pub(crate) fn native_data() -> &'static NativeCsData {
        unsafe {
            let ptr = NATIVE_DATA.with(|x|  *x.get());
            assert_ne!(ptr, null(), "NATIVE DATA NOT SET");
            &*ptr
        }
    }

    pub fn prompt_enabled() -> bool {
        (native_data().prompt_enabled)()
    }

    pub fn display_dialog(title: &str, message: &str, ok: &str, cancel: &str) -> bool {
        (native_data().display_dialog)(
            &RsStr::new(title),
            &RsStr::new(message),
            &RsStr::new(ok),
            &RsStr::new(cancel),
        )
    }

    pub fn log_error(message: &str) {
        (native_data().log_error)(&RsStr::new(message));
    }

    pub fn guid_to_asset_path(guid: &[u8; 128 / 8]) -> &'static str {
        let mut ptrs = [0usize; 2];
        (native_data().guid_to_asset_path)(guid, &mut ptrs);
        if ptrs[1] == 0 {
            return "" // to avoid nullptr on pointer
        }
        unsafe {
            std::str::from_utf8_unchecked(
                std::slice::from_raw_parts(ptrs[0] as *const u8, ptrs[1])
            )
        }
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
    let client = reqwest_cs::Client::new();
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

    if prompt_enabled() {
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

        if !display_dialog("Confirm", &confirm_message, "Install", "Cancel") {
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
