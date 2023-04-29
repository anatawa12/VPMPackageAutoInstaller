use std::io::{Cursor, Read, Result, Write};

#[cfg(target_arch = "wasm32")]
pub extern "C" fn create_unitypackage_wasm(
    template_ptr: *const u8, 
    template_len: usize, 
    json_ptr: *const u8,
    json_len: usize,
) -> *const () {
    #[allow(dead_code)]
    struct Result {
        ptr: *const u8,
        len: usize,
        is_err: bool,
    }

    let template = unsafe { std::slice::from_raw_parts(template_ptr, template_len) };
    let json = unsafe { std::slice::from_raw_parts(json_ptr, json_len) };
    let mut out_buf = Vec::<u8>::new();

    let result: (Vec<u8>, bool);
    match create_unitypackage(template, &mut out_buf, json) {
        Ok(()) => {
            // ok: return buffer
            result = (out_buf, false);
        }
        Err(err) => {
            result = (err.to_string().into_bytes(), false);
        }
    }

    let leak = result.0.leak();

    let boxed = Box::new(Result {
        ptr: leak.as_ptr(),
        len: leak.len(),
        is_err: result.1,
    });

    Box::leak(boxed) as *const Result as _
}

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
