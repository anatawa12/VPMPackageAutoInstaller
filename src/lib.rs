use std::io::{Cursor, Read, Result, Write};

pub fn create_unitypackage(template: &[u8], output: impl Write, json: &[u8]) -> Result<()> {
    let decoder = flate2::read::GzDecoder::new(Cursor::new(template));
    let encoder = flate2::write::GzEncoder::new(output, flate2::Compression::default());

    make_tar_with_json(decoder, encoder, json)
}

fn make_tar_with_json(template: impl Read, output: impl Write, json: &[u8]) -> Result<()> {
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

    Ok(())
}
