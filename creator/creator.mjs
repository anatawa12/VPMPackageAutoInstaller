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

let INSTALLER_VERSION = "1.0.9-SNAPSHOT";

// initialize wasm module
let wasm_array = await wasm_binary();
let {module, instance} = await WebAssembly.instantiate(wasm_array, {});

/**
 * @param json {string}
 * @return {Uint8Array} content
 */
function create_unitypackage(json) {
  const {memory, alloc_memory, free_memory, create_unitypackage_wasm} = instance.exports;

  // minify
  const json_min = JSON.stringify(JSON.parse(json));

  // pass JSON to memory
  const json_bin = new TextEncoder().encode(json_min);
  const ptr = alloc_memory(json_bin.byteLength);
  var json_buf = new Uint8Array(memory.buffer, ptr, json_bin.byteLength);
  json_buf.set(json_bin);

  const result_ptr = create_unitypackage_wasm(ptr, json_bin.byteLength);
  free_memory(ptr);

  const [msg_ptr, msg_len] = new Uint32Array(memory.buffer, result_ptr);
  const is_error = !!(new Uint8Array(memory.buffer, result_ptr + 4 * 2)[0]);

  if (is_error) {
    // if error, throw that.
    const msg = new TextDecoder().decode(new Uint8Array(memory.buffer, msg_ptr, msg_len));
    free_memory(msg_ptr);
    throw new Error(msg);
  }

  return new Uint8Array(memory.buffer, msg_ptr, msg_len);
}

if (typeof Deno != "undefined") {
  //console.info("deno detected");
  const [configPath, outputPath] = Deno.args;

  if (!configPath || !outputPath) {
    console.error(`deno run ${import.meta.url} {configPath} {outputPath}`);
    Deno.exit(-1);
  }
  await Deno.permissions.request({name: "read", path: configPath});
  await Deno.permissions.request({name: "write", path: outputPath});

  const decoder = new TextDecoder("utf-8");
  const config_bin = await Deno.readFile(configPath);
  const config = decoder.decode(config_bin);

  const gzipped = create_unitypackage(config);

  await Deno.writeFile(outputPath, gzipped);
} else if (typeof process !== "undefined") {
  //console.info("nodejs detected");

  const [_node, _script, configPath, outputPath] = process.argv;

  if (!configPath || !outputPath) {
    console.error(`${_node} ${_script} {configPath} {outputPath}`);
    process.exit(-1);
  }

  const fs = await import('node:fs/promises');

  const config = await fs.readFile(configPath, { encoding: 'utf8' });

  const gzipped = create_unitypackage(config);

  await fs.writeFile(outputPath, gzipped);

} else if (typeof window != "undefined") {
  //console.info("browser detected");
  window.create = async function create() {
    try {
      let config = document.getElementById("config").value;
      config = JSON.stringify(JSON.parse(config)); // minify
      const a = document.createElement('a');
      a.href = URL.createObjectURL(new Blob([create_unitypackage(config)]));
      a.download = "installer.unitypackage"
      a.textContent = "test"
      a.click()
    } catch (e) {
      alert("error creating installer");
      throw e;
    }
  }

  document.getElementById("vpai-version-field").textContent = `using VPAI version ${INSTALLER_VERSION}`;
} else {
  throw new Error("unsupported runtime");
}

async function wasm_binary() {
  const base64 = "<BASE64-INJECTED>"; // BASE64-INJECT-LINE

  if (base64 == ("<BASE64-INJECTED>")) {
    return new Uint8Array(await (await fetch(new URL("./vpai_creator.wasm", import.meta.url))).arrayBuffer());
  }

  return Uint8Array.from(atob(base64), c => c.charCodeAt(0))
}
