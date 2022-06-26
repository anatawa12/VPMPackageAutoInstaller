AutoPackageInstaller
===

An experimental project to install unity package published via non-official registry with unitypackage file.

## How to use

### Editor Plugin

1. download latest version of installer creator [here][download-creator-latest]
2. Import the unitypackage to the unity project contains ``package.json``
3. Open `AutoPackageInstallerCreator` from Window menu
4. Select package.json
5. If not correct, please set git url & git tag.
6. Click `Create Installer`

[download-creator-latest]: https://github.com/anatawa12/AutoPackageInstaller/releases/latest/download/installer-creator.unitypackage

### Other ways

<details>
<summary>click to open other ways</summary>

#### CLI Tool

1. Download latest version of installer creator [here][download-creator-js-latest].
2. Create config.json
3. Run `node path/to/creator.mjs path/to/config.json path/to/output.unitypackage` or
    `deno run --allow-net --allow-read --allow-write path/to/creator.mjs path/to/config.json path/to/output.unitypackage`

#### Web tool

1. Open website [here][creator-web]
2. Write config.json
3. Click `create installer`

[creator-web]: https://anatawa12.github.io/AutoPackageInstaller/

[download-creator-js-latest]: https://github.com/anatawa12/AutoPackageInstaller/releases/latest/download/creator.mjs

#### Just create unitypackage

1. Clone or [download][download-this] this project.
2. Edit config.json at `Assets/com.anatawa12.auto-package-installer/config.json`
3. Open this project with Unity (if you did, there's no need to relaunch)
4. Select `Assets/com.anatawa12.auto-package-installer` in Unity's File view
5. Left-click on `Assets/com.anatawa12.auto-package-installer` and click `Export Package`
6. un-check `Incliude dependencies` on `Exporting package` dialog.
7. Click `Export...` and save `.unitypackage` to anywhere you want.
8. Share `.unitypackage` with user who want to use your package.

</details>

[download-this]: https://github.com/anatawa12/AutoPackageInstaller/archive/refs/heads/master.zip

## Config format

The format of `config.json` is almost same as [`manifest.json`][manifest-json-unity] but 
only `dependencies` tag is supported.

[manifest-json-unity]: https://docs.unity3d.com/current/Manual/upm-manifestPrj.html

### Example

This example shows installer package for <https://github.com/anatawa12/VRC-Unity-extension>.
Please replace `https://github.com/anatawa12/VRC-Unity-extension.git` and `com.anatawa12.editor-extension`
with your git repository and package name.
If your package has some dependency packages on git, you should add to dependencies block.

```json
{
  "dependencies": {
    "com.anatawa12.editor-extension": "https://github.com/anatawa12/VRC-Unity-extension.git"
  }
}

```

## How this works

This uses `InitializeOnLoad` attribute to run some script on unpacked `unitypackage` and
on `InitializeOnLoad`, modifies `manifest.json` based on `config.json`. 
Just after modification, this package deletes files & folders of this project based on `GUID`.
GUID of C#, `config.json`, and `com.anatawa12.auto-package-installer` are hard-coded.
