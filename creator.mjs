#!/usr/bin/env node
/**
 * VPMPackageAutoInstaller Creator
 *
 * How To Use:
 * A) use node.js
 * $ node path/to/this.mjs <path/to/your/config.json> <path/to/result.unitypackage>
 * B) use deno
 * $ deno run --allow-net --allow-read --allow-write \
 *   path/to/this.mjs <path/to/your/config.json> <path/to/result.unitypackage>
 * # net is required to download template, read to read config, write to write result unitypackage.
 *
 * This file also work as main mjs file of index.html.
 */

/**
 * FOR DEVELOPER
 * If you changed this file, you may also need to update Creator.cs
 */

/**
 * @param template {Uint8Array}
 * @param json {Uint8Array}
 * @return {Uint8Array}
 */
function makeTarWithJson(template, json) {
  // magic numbers
  const chunkLen = 512;
  const nameOff = 0;
  const nameLen = 100;
  const sizeOff = 124;
  const sizeLen = 12;
  const checksumOff = 148;
  const checksumLen = 8;
  const configJsonPathInTar = "./9028b92d14f444e2b8c389be130d573f/asset";

  let cursor = 0
  while (cursor < template.byteLength) {
    const size = readOctal(template, cursor + sizeOff, sizeLen);
    const contentSize = Math.ceil(size / chunkLen) * chunkLen;
    const name = readString(template, cursor + nameOff, nameLen);
    if (name === configJsonPathInTar) {
      // set new size and calc checksum
      saveOctal(template, cursor + sizeOff, sizeLen, json.length, sizeLen - 1);
      template.fill(' '.charCodeAt(0), cursor + checksumOff, cursor + checksumOff + checksumLen);
      const checksum = calcCheckSum(refSlice(template, cursor, chunkLen))
      saveOctal(template, cursor + checksumOff, checksumLen, checksum, checksumLen - 2);

      // calc pad size
      const padSize = json.length % chunkLen === 0 ? 0 : (chunkLen - json.length);

      // create tar file
      const result = new Uint8Array((cursor + chunkLen)
        + json.length + padSize
        + (template.length - (cursor + chunkLen + contentSize)))
      result.set(refSlice(template, 0, cursor + chunkLen), 0);
      result.set(json, cursor + chunkLen);
      // there's no need to set padding because already 0-filled
      result.set(refSlice(template, cursor + chunkLen + contentSize),
        (cursor + chunkLen) + json.length + padSize)
      return result;
    } else {
      cursor += chunkLen;
      cursor += contentSize;
    }
  }
  throw new Error("config.json not found");
}

/**
 *
 * @param buf {Uint8Array}
 * @param {number} [offset]
 * @param {number} [length]
 * @return {Uint8Array}
 */
function refSlice(buf, offset = 0, length = buf.byteLength - offset) {
  if (offset > buf.byteLength) {
    throw new Error(`index out of bounds: len: ${buf.byteLength} offset: ${offset}`)
  }
  if (offset + length > buf.byteLength) {
    throw new Error(`index out of bounds: len: ${buf.byteLength} offset: ${offset}, length: ${length}`)
  }

  return new Uint8Array(buf.buffer, buf.byteOffset + offset, length)
}

/**
 * @param {Uint8Array} buf
 * @return {number}
 */
function calcCheckSum(buf) {
  let sum = 0;
  for (let i = 0; i < 512; i++) {
    sum = (sum + buf[i]) & 0x1FFFF;
  }
  return sum;
}

/**
 * @param {Uint8Array} buf
 * @param {number} offset
 * @param {number} len
 * @return {string}
 */
function readString(buf, offset, len) {
  let findRange = refSlice(buf, offset, len);
  let firstNullByte = findRange.indexOf(0);
  if (firstNullByte === -1)
    return new TextDecoder().decode(findRange);
  return new TextDecoder().decode(refSlice(findRange, 0, firstNullByte));
}

/**
 * @param {Uint8Array} buf
 * @param {number} offset
 * @param {number} len
 * @return {number}
 */
function readOctal(buf, offset, len) {
  const s = readString(buf, offset, len);
  if (s === "") return 0;
  return parseInt(s, 8)
}

/**
 * @param {Uint8Array} buf
 * @param {number} offset
 * @param {number} len
 * @param {number} value
 * @param {number} octalLen
 */
function saveOctal(buf, offset, len, value, octalLen = 0) {
  const str = value.toString(8).padStart(octalLen, '0');
  const part = refSlice(buf, offset, len);
  const res = new TextEncoder().encodeInto(str, part);

  if (res.read !== str.length)
    throw new Error(`space not enough (${res.read}, ${str.length})`)

  if (res.written < part.length) {
    part[res.written] = 0;
    part.fill(' '.charCodeAt(0), res.written + 1, part.length);
  }
}

let INSTALLER_VERSION = "0.2.8";

if (typeof Deno != "undefined") {
  //console.info("deno detected");
  const [configPath, outputPath] = Deno.args;

  if (!configPath || !outputPath) {
    console.error(`deno run ${import.meta.url} {configPath} {outputPath}`);
    Deno.exit(-1);
  }
  await Deno.permissions.request({name: "read", path: configPath});
  await Deno.permissions.request({name: "write", path: outputPath});

  let templateUrl;
  if (INSTALLER_VERSION.endsWith("-SNAPSHOT") || Number(Deno.env.USE_LOCAL_PREBUILT)) {
    templateUrl = new URL("../installer-template.unitypackage", import.meta.url);
    if (templateUrl.protocol === 'file:')
      await Deno.permissions.request({name: "read", path: templateUrl.pathname});
    else
      await Deno.permissions.request({name: "net", host: templateUrl.host});
  } else {
    templateUrl = `https://github.com/anatawa12/VPMPackageAutoInstaller/`
      + `releases/download/v${INSTALLER_VERSION}/installer-template.unitypackage`
    await Deno.permissions.request({name: "net", host: 'github.com'});
  }

  await Deno.permissions.request({name: "net", host: 'deno.land'});

  // use + to make lazy load
  const compress = await import("https://deno.land/x/" + "compress@v0.4.5/mod.ts");

  const fetched = await fetch(templateUrl);
  const template = new Uint8Array(await fetched.arrayBuffer());
  const config = await Deno.readFile(configPath);

  const templateGUnzipped = compress.gunzip(template)
  const output = makeTarWithJson(templateGUnzipped, config);
  const gzipped = compress.gzip(output);
  await Deno.writeFile(outputPath, gzipped);
} else if (typeof process !== "undefined") {
  //console.info("nodejs detected");

  const [_node, _script, configPath, outputPath] = process.argv;

  if (!configPath || !outputPath) {
    console.error(`${_node} ${_script} {configPath} {outputPath}`);
    process.exit(-1);
  }

  const fs = await import('node:fs/promises');
  const url = await import('node:url');
  const zlib = await import('node:zlib');
  const https = await import('node:https');
  const {promisify} = await import('node:util');

  let template;
  if (INSTALLER_VERSION.endsWith("-SNAPSHOT") || Number(process.env.USE_LOCAL_PREBUILT)) {
    const path = url.fileURLToPath(new URL("../build/installer-template.unitypackage", import.meta.url))
    template = await fs.readFile(path);
  } else {
    let url = `https://github.com/anatawa12/VPMPackageAutoInstaller/`
      + `releases/download/v${INSTALLER_VERSION}/installer-template.unitypackage`;
    while (true) {
      const res = await new Promise(resolve => https.get(url, (res) => resolve(res)));
      if (res.statusCode === 200) {
        const chunks = []
        for await (let chunk of res) {
          chunks.push(chunk)
        }
        template = Buffer.concat(chunks);
        break;
      } else if (res.statusCode === 301 || res.statusCode === 302 || res.statusCode === 303
        || res.statusCode === 307 || res.statusCode === 308) {
        url = new URL(res.headers.location, url);
      } else {
        throw new Error(`Unsupported status: ${res.statusCode}`);
      }
    }
  }

  const config = await fs.readFile(configPath);
  const templateGUnzipped = await promisify(zlib.gunzip)(template);
  const output = makeTarWithJson(templateGUnzipped, config);
  const gzipped = await promisify(zlib.gzip)(output);
  await fs.writeFile(outputPath, gzipped);

} else if (typeof window != "undefined") {
  //console.info("browser detected");
  async function downloadTemplate() {
    const res = await fetch("./installer-template.unitypackage", {
      mode: 'same-origin',
    });
    return new Uint8Array(await res.arrayBuffer());
  }

  const wasmGzipQueried = import("https://cdn.jsdelivr.net/npm/" + "wasm-gzip@1.0.1/wasm_gzip.js");

  async function createPackage(config) {
    const wasm_gzip = await wasmGzipQueried;
    const init = wasm_gzip.default;
    const {compressGzip, decompressGzip} = wasm_gzip;

    const [_, template] = await Promise.all([init(), downloadTemplate()]);
    return compressGzip(makeTarWithJson(decompressGzip(template), new TextEncoder().encode(config)));
  }

  window.create = async function create() {
    try {
      const config = document.getElementById("config").value;
      const a = document.createElement('a');
      a.href = URL.createObjectURL(new Blob([await createPackage(config)]));
      a.download = "installer.unitypackage"
      a.textContent = "test"
      a.click()
    } catch (e) {
      alert("error creating installer");
      throw e;
    }
  }
} else {
  throw new Error("unsupported runtime");
}

