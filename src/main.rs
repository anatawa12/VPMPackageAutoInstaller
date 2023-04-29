use std::env::args;
use std::io;
use std::io::{Cursor, Read, Write};
use std::process::exit;

fn help_die(exe: &str) -> ! {
    eprintln!("{exe} {{configPath}} {{outputPath}}");
    exit(1)
}

fn main() {
    env_logger::init();
    let mut args = args();
    let exe = args.next().unwrap();
    let config_path = args.next().unwrap_or_else(|| help_die(&exe));
    let output_path = args.next().unwrap_or_else(|| help_die(&exe));

    let response = reqwest::blocking::get(concat!(
        "https://github.com/anatawa12/VPMPackageAutoInstaller/releases/download/v",
        env!("CARGO_PKG_VERSION"),
        "/installer-template.unitypackage",
    ))
    .expect("downloading template")
    .bytes()
    .expect("downloading template");

    let json = std::fs::read_to_string(config_path).expect("reading json");
    let minified = minify::json::minify(&json);

    let output = std::fs::File::create(output_path).expect("creating output");

    let decoder = flate2::read::GzDecoder::new(Cursor::new(response));
    let encoder = flate2::write::GzEncoder::new(output, flate2::Compression::default());
    make_tar_with_json(decoder, encoder, minified.as_bytes()).expect("creating unity package");
}

/**
 * @param template {Uint8Array}
 * @param json {Uint8Array}
 * @return {Uint8Array}
 */
fn make_tar_with_json(template: impl Read, output: impl Write, json: &[u8]) -> io::Result<()> {
    let mut read_tar = tar::Archive::new(template);
    let mut write_tar = tar::Builder::new(output);

    for e in read_tar.entries()? {
        let e = e?;
        let path = e.path_bytes();
        if path.strip_prefix(b"./").unwrap_or(path.as_ref())
            == b"9028b92d14f444e2b8c389be130d573f/asset"
        {
            let mut header = e.header().clone();
            header.set_size(json.len() as u64);
            header.set_cksum();
            write_tar.append(&header, json)?;
        } else {
            write_tar.append(&e.header().clone(), e)?;
        }
    }

    write_tar.finish()?;
    write_tar.get_mut().flush()?;

    Ok(())
}
