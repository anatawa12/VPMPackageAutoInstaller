use std::fmt::{Debug, Display, Formatter, Write};
use std::future::Future;
use std::marker::PhantomPinned;
use std::pin::Pin;
use std::rc::Rc;
use std::sync::atomic::{AtomicU32, Ordering};
use std::task::{Context, Poll, ready, Waker};
use std::task::Poll::{Pending, Ready};
use futures::Stream;
use serde::de::DeserializeOwned;
use serde::{Deserialize, Deserializer};
use crate::interlop::{CsHandle, CsSlice, CsStr, native_data, RsStr};

macro_rules! async_wrapper {
    (
        struct $struct_name: ident $(< $($lifetime: lifetime),+ >)? -> $return: ty {
            $($field: ident: $field_ty: ty $(= $field_value: expr)?),* $(,)?
        },
        |$this: ident, $wake: ident| $call_native: expr,
        |$this1: ident| $result: expr $(,)?
    ) => {
        struct $struct_name $(< $($lifetime),+ >)? {
            $($field: $field_ty,)*
            state: AtomicU32,
            waker: Option<Waker>,
            _pinned: PhantomPinned,
        }

        impl $(< $($lifetime),+ >)? ::std::future::Future for $struct_name $(< $($lifetime),+ >)? {
            type Output = $return;

            fn poll(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Self::Output> {
                let $this = unsafe { self.get_unchecked_mut() };
                loop {
                    match $this.state.load(Ordering::Acquire) {
                        Self::INITIAL => {
                            $this.waker = Some(cx.waker().clone());
                            $this.state.store(Self::POLLING, Ordering::Relaxed);
                            {
                                let $wake = Self::wake;
                                $call_native;
                            }
                        }
                        Self::POLLING => {
                            return Pending
                        }
                        Self::FINISHED => {
                            let $this1 = $this;
                            return Poll::Ready($result)
                        }
                        _ => unreachable!(),
                    }
                }
            }
        }
        impl $(< $($lifetime),+ >)? $struct_name $(< $($lifetime),+ >)? {
            const INITIAL: u32 = 0;
            const POLLING: u32 = 1;
            const FINISHED: u32 = 2;
            fn wake(ptr: *const ()) {
                let this = unsafe { &mut *(ptr as *mut Self) };
                this.state.store(Self::FINISHED, Ordering::Release);
                if let Some(waker) = this.waker.take() {
                    waker.wake();
                }
            }
        }

        async_wrapper! {
            @call_optional $struct_name $(< $($lifetime),+ >)? $($field: $field_ty $(= $field_value)?,)* 
        }
    };

    (@call_optional $struct_name: ident $(< $($lifetime: lifetime),+ >)? $($field: ident: $field_ty: ty = $field_value: expr,)*) => {
        $struct_name {
            $($field: $field_value,)*
            state: AtomicU32::new($struct_name::INITIAL),
            waker: None,
            _pinned: PhantomPinned,
        }
    };

    (@call_optional $struct_name: ident $($($lifetime: lifetime),+)? $($field: tt)*) => {
        // create constructor instead
        async_wrapper! {@build_new 
            [$struct_name]
            [$(< $($lifetime),+ >)?]
            []
            []
            [$($field)*] 
        }
    };

    (@build_new
        [$struct_name: ident] 
        [$($lifetime: tt)*]
        [$($field_init: tt)*]
        [$($field_param: tt)*]
        [$field_cur: ident: $field_ty_cur: ty, $($rest: tt)*]
    ) => {
        async_wrapper! { @build_new
            [$struct_name]
            [$($lifetime)*]
            [$($field_init)*]
            [$($field_param)* $field_cur: $field_ty_cur,]
            [$($rest)*]
        }
    };

    (@build_new
        [$struct_name: ident] 
        [$($lifetime: tt)*]
        [$($field_init: tt)*]
        [$($field_param: tt)*]
        [$field_cur: ident: $field_ty_cur: ty = $field_value_cur: expr, $($rest: tt)*]
    ) => {
        async_wrapper! { @build_new
            [$struct_name]
            [$($lifetime)*]
            [$($field_init)*  $field_cur: $field_ty_cur = $field_value_cur,]
            [$($field_param)*]
            [$($rest)*]
        }
    };

    (@build_new
        [$struct_name: ident] 
        [$($($lifetime: lifetime),+)?]
        [$($field_init: ident: $field_ty_init: ty = $field_value_init: expr,)*]
        [$($field_param: ident: $field_ty_param: ty,)*]
        []
    ) => {
        // create constructor instead
        impl $(< $($lifetime),+ >)? $struct_name $(< $($lifetime),+ >)? {
            pub fn new($($field_param: $field_ty_param,)*) -> Self {
                Self {
                    $($field_param,)*
                    $($field_init: $field_value_init,)*
                    state: AtomicU32::new(Self::INITIAL),
                    waker: None,
                    _pinned: PhantomPinned,
                }
            }
            
            pub unsafe fn reset(self: Pin<&mut Self>) {
                let this = self.get_unchecked_mut();
                $(this.$field_init = $field_value_init;)*
                this.state = AtomicU32::new(Self::INITIAL);
                this.waker = None;
            }
        }
    };
}

// client instance is shared
#[derive(Clone)]
pub struct Client {
    ptr: Rc<CsHandle>
}

impl Client {
    pub fn new() -> Self {
        Self {
            ptr: Rc::new((native_data().web_client_new)(&RsStr::new(env!("CARGO_PKG_VERSION"))))
        }
    }

    pub fn get(&self, url: impl IntoUrl) -> Request {
        match url.into_url() {
            Ok(url) => {
                Request {
                    ptr: Ok((native_data().web_request_new_get)((*self.ptr).as_ref(), &RsStr::new(url.as_str())))
                }
            }
            Err(err) => {
                Request { ptr: Err(err) }
            }
        }
    }
}

impl Debug for Client {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        f.write_str("Client { cs stab }")
    }
}


pub struct Request {
    ptr: Result<CsHandle>,
}

impl Request {
    pub fn header(mut self, name: &str, value: &str) -> Self {
        if let Ok(ptr) = self.ptr.as_ref().map(|x| x.as_ref()) {
            let mut err = CsStr::invalid();
            (native_data().web_request_add_header)(ptr, &RsStr::new(name), &RsStr::new(value), &mut err);
            if err.is_invalid() {
                self.ptr = Err(Error::cs(err.to_string()));
            }
        }
        self
    }

    pub fn send(self) -> impl Future<Output=Result<Response>> {
        async_wrapper! {
            struct Future -> Result<Response> {
                ptr: Result<CsHandle> = self.ptr,
                result: CsHandle = CsHandle::invalid(),
                err: CsStr = CsStr::invalid(),
            },
            |this, wake| {
                let this_ptr = this as *const _ as *const ();
                match std::mem::replace(&mut this.ptr, Ok(CsHandle::invalid())) {
                    Ok(ptr) => {
                        (native_data().web_request_send)(ptr, &mut this.result, &mut this.err, this_ptr, wake)
                    }
                    Err(err) => return Ready(Err(err))
                };
            },
            |this| {
                if this.err.as_str() != "" {
                    Err(Error::cs(this.err.to_string()))
                } else {
                    Ok(Response { ptr: this.result.take() })
                }
            }
        }
    }
}

pub struct Response {
    ptr: CsHandle,
}

impl Response {
    pub fn status(&self) -> u32 {
        (native_data().web_response_status)(self.ptr.as_ref())
    }

    pub fn error_for_status(self) -> Result<Self> {
        let status = self.status();
        if matches!(status, 400..=599) {
            Ok(self)
        } else {
            Err(Error::status_code(status))
        }
    }

    pub fn headers(&self) -> HeaderMap {
        HeaderMap {
            ptr: (native_data().web_response_headers)(self.ptr.as_ref()),
        }
    }

    pub fn bytes_stream(self) -> impl Stream<Item = Result<CsSlice<u8>>>  {
        AsStream {
            inner: Box::pin(StreamInner::new((native_data().web_response_async_reader)(self.ptr))),
        }
    }

    pub fn bytes(self) -> impl Future<Output=Result<CsSlice<u8>>> {
        async_wrapper! {
            struct Future -> Result<CsSlice<u8>> {
                ptr: CsHandle = self.ptr,
                slice: CsSlice<u8> = CsSlice::invalid(),
                err: CsStr = CsStr::invalid(),
            },
            |this, wake| {
                let this_ptr = this as *const _ as *mut ();
                (native_data().web_response_bytes_async)(
                    this.ptr.take(),
                    &mut this.slice,
                    &mut this.err,
                    this_ptr,
                    wake,
                )
            },
            |this| {
                if this.err.is_invalid() {
                    Err(Error::cs(this.err.to_string()))
                } else {
                    Ok(this.slice.take())
                }
            }
        }
    }

    pub async fn json<T : DeserializeOwned>(self) -> Result<T> {
        serde_json::from_slice(&self.bytes().await?)
            .map_err(Error::json)
    }
}

pub struct HeaderMap {
    pub ptr: CsHandle,
}

impl HeaderMap {
    pub fn get(&self, name: &str) -> Option<HeaderValue> {
        let mut result = CsStr::invalid();
        (native_data().web_headers_get)(self.ptr.as_ref(), &RsStr::new(name), &mut result);
        if result.is_invalid() {
            None
        } else {
            Some(HeaderValue { str: result })
        }
    }
}

pub struct HeaderValue {
    str: CsStr,
}

impl HeaderValue {
    pub fn to_string(&self) -> Result<String> {
        Ok(self.str.to_string())
    }
}

async_wrapper! {
    struct StreamInner -> Result<CsSlice<u8>> {
        ptr: CsHandle,
        slice: CsSlice<u8> = CsSlice::invalid(),
        err: CsStr = CsStr::invalid(),
    },
    |this, wake| {
        let this_ptr = this as *const _ as *mut ();
        (native_data().web_async_reader_read)(
            this.ptr.as_ref(),
            &mut this.slice,
            &mut this.err,
            this_ptr,
            wake,
        )
    },
    |this| {
        if this.err.as_str() != "" {
            Err(Error::cs(this.err.to_string()))
        } else {
            Ok(this.slice.take())
        }
    }
}

pub struct AsStream {
    inner: Pin<Box<StreamInner>>,
}

impl Stream for AsStream {
    type Item = Result<CsSlice<u8>>;

    fn poll_next(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Option<Self::Item>> {
        let this = self.get_mut();
        let ready = ready!(this.inner.as_mut().poll(cx));

        unsafe {
            this.inner.as_mut().reset();
        }

        return match ready {
            Ok(r) => {
                if r.len() == 0 {
                    // means end of stream
                    Ready(None)
                } else {
                    Ready(Some(Ok(r)))
                }
            }
            Err(e) => {
                Ready(Some(Err(e)))
            }
        }
    }
}

type Result<R> = std::result::Result<R, Error>;

pub trait IntoUrl {
    fn into_url(self) -> Result<Url>;
}

impl IntoUrl for Url {
    fn into_url(self) -> Result<Url> {
        Ok(self)
    }
}

impl IntoUrl for &str {
    fn into_url(self) -> Result<Url> {
        self.parse::<Url>()?.into_url()
    }
}

impl IntoUrl for &String {
    fn into_url(self) -> Result<Url> {
        (&**self).into_url()
    }
}

#[derive(Debug)]
pub struct Error {
    inner: Box<Inner>,
}

#[derive(Debug)]
enum Inner {
    BadScheme(Url),
    ErrorStatusCode(u32),
    CSharpError(String),
    JsonError(serde_json::Error),
    ParseUrlError,
}

impl Error {
    fn new(inner: Inner) -> Self {
        Self { inner: Box::new(inner) }
    }
    fn url_bad_scheme(url: Url) -> Error {
        Self::new(Inner::BadScheme(url))
    }
    fn status_code(code: u32) -> Error {
        Self::new(Inner::ErrorStatusCode(code))
    }
    fn cs(msg: String) -> Error {
        Self::new(Inner::CSharpError(msg))
    }
    fn json(err: serde_json::Error) -> Error {
        Self::new(Inner::JsonError(err))
    }
    fn parse_err() -> Error {
        Self::new(Inner::ParseUrlError)
    }
}

impl Display for Error {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        match &*self.inner {
            Inner::BadScheme(_) => f.write_str("URL scheme is not allowed"),
            Inner::ErrorStatusCode(code) => {
                if matches!(code, 400..=499) {
                    write!(f, "HTTP status client error ({})", code)
                } else {
                    write!(f, "HTTP status server error ({})", code)
                }
            }
            Inner::CSharpError(msg) => f.write_str(msg),
            Inner::JsonError(e) => Display::fmt(e, f),
            Inner::ParseUrlError => f.write_str("http/https url expected"),
        }
    }
}

impl std::error::Error for Error {}

#[derive(Debug, Clone)]
pub struct Url {
    url: String,
}

impl Url {
    pub fn as_str(&self) -> &str {
        self.url.as_str()
    }
}

impl std::str::FromStr for Url {
    type Err = Error;

    fn from_str(s: &str) -> std::result::Result<Self, Self::Err> {
        if (native_data().verify_url)(&RsStr::new(s)) {
            Ok(Url { url: s.to_owned() })
        } else {
            Err(Error::parse_err())
        }
    }
}

impl AsRef<str> for Url {
    fn as_ref(&self) -> &str {
        self.as_str()
    }
}

impl Display for Url {
    fn fmt(&self, f: &mut Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.as_str())
    }
}

impl <'de> Deserialize<'de> for Url {
    fn deserialize<D>(deserializer: D) -> std::result::Result<Self, D::Error> where D: Deserializer<'de> {
        use serde::de::{Error, Unexpected, Visitor};

        struct UrlVisitor;

        impl<'de> Visitor<'de> for UrlVisitor {
            type Value = Url;

            fn expecting(&self, formatter: &mut Formatter) -> std::fmt::Result {
                formatter.write_str("a string representing an URL")
            }

            fn visit_str<E>(self, s: &str) -> std::result::Result<Self::Value, E>
                where
                    E: Error,
            {
                s.parse().map_err(|_| Error::invalid_value(Unexpected::Str(s), &"http/https url expected"))
            }
        }

        deserializer.deserialize_str(UrlVisitor)
    }
}
