use std::borrow::Cow;
use std::collections::HashMap;
use std::result;
use percent_encoding::{NON_ALPHANUMERIC, percent_decode_str, utf8_percent_encode};
use serde_json::json;
use vpai_creator::create_unitypackage;
use worker::*;

mod utils;

fn log_request(req: &Request) {
    console_log!(
        "{} - [{}], located at: {:?}, within: {}",
        Date::now().to_string(),
        req.path(),
        req.cf().coordinates().unwrap_or_default(),
        req.cf().region().unwrap_or_else(|| "unknown region".into())
    );
}

#[event(fetch)]
pub async fn main(mut req: Request, env: Env, _ctx: Context) -> Result<Response> {
    log_request(&req);
    utils::set_panic_hook();

    if matches!(req.method(), Method::Post) {
        return Response::error("Method Not Allowed", 405);
    }

    let mut repo_urls = Vec::new();
    let mut package = None;
    let mut version = None;
    let mut prerelease = false;
    let mut name = Cow::from("");

    let url = req.url()?;
    for (key, value) in url.query_pairs() {
        match key.as_ref() {
            "repo" | "repos[]" => repo_urls.push(value),
            "package" => package = Some(value),
            "version" => version = Some(value),
            "prerelease" => match value.as_ref() {
                "true" | "1" | "" => prerelease = true,
                "false" | "0" => prerelease = false,
                _ => return Response::error("bad prerelease param. true|false|1|0 expected", 400),
            }
            "name" => name = value,
            _ => {}
        }
    }

    let Some(package) = package else {
        return Response::error("get param package is required", 400);
    };

    let Some(version) = version else {
        return Response::error("get param version is required", 400);
    };

    // generate

    let json = json!({
        "vpmRepositories": repo_urls,
        "vpmDependencies": { package: version },
        "includePrerelease": prerelease
    });

    let json = serde_json::to_string(&json)?;

    let mut response = Vec::<u8>::new();
    create_unitypackage(&mut response, json.as_bytes())
        .map_err(|e| Error::RustError(format!("creating unitypackage {e}")))?;

    if name.is_empty() {
        name = Cow::from("installer.unitypackage");
    } else if !name.ends_with(".unitypackage") {
        name = Cow::Owned(format!("{}.unitypackage", name));
    }

    let name = name.replace("{}", &version);

    let mut response = Response::from_bytes(response)?;

    response.headers_mut().set(
        "Content-Disposition",
        &format!("attachment; filename*=utf8''{}", utf8_percent_encode(&name, NON_ALPHANUMERIC)),
    )?;
    Ok(response)
}
