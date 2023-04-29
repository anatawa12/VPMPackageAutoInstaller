use std::env::args;
use std::io::Cursor;
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
    vpai_creator::make_tar_with_json(decoder, encoder, minified.as_bytes())
        .expect("creating unity package");
}
