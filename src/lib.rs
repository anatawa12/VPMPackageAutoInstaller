use std::io::{Read, Result, Write};

/**
 * @param template {Uint8Array}
 * @param json {Uint8Array}
 * @return {Uint8Array}
 */
pub fn make_tar_with_json(template: impl Read, output: impl Write, json: &[u8]) -> Result<()> {
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
