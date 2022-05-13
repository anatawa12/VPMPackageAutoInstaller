AutoPackageInstaller
===

An experimental project to install unity package published via non-official registry with unitypackage file.

## How to use

Easier way will be provided on web but not yet.

1. Clone or [download][download-this] this project.
2. Edit config.json at `Assets/com.anatawa12.auto-package-installer/config.json`
3. Open this project with Unity (if you did, there's no need to relaunch)
4. Select `Assets/com.anatawa12.auto-package-installer` in Unity's File view
5. Left-click on `Assets/com.anatawa12.auto-package-installer` and click `Export Package`
6. un-check `Incliude dependencies` on `Exporting package` dialog.
7. Click `Export...` and save `.unitypackage` to anywhere you want.
8. Share `.unitypackage` with user who want to use your package.

[download-this]: https://github.com/anatawa12/AutoPackageInstaller/archive/refs/heads/master.zip

## Config format

The format of `config.json` is almost same as [`manifest.json`][manifest-json-unity] but 
only `dependencies` and `scopedRegistries` tag are supported.

[manifest-json-unity]: https://docs.unity3d.com/current/Manual/upm-manifestPrj.html

### Examples

<!--
TODO: upload my package to openupm and uncomment

#### [OpenUPM][openupm] provided packages

This example shows installer package for [com.anatawa12.editor-extension on openupm][anatawa12-editor-extension-openupm].
Please replace `"com.anatawa12.editor-extension": "0.1.0"` with your package name & version.
If you have some dependencies on your package, please add to "dependencies" block.

[anatawa12-editor-extension-openupm]: https://openupm.com/packages/com.anatawa12.editor-extension 
[openupm]: https://openupm.com/

```json
{
  "scopedRegistries": [
    {
      "name": "OpenUPM",
      "url": "https://package.openupm.com",
      "scopes": [
        "com.anatawa12"
      ]
    }
  ],
  "dependencies": {
    "com.anatawa12.editor-extension": "0.1.0"
  }
}
```
-->

#### Git-provided packages

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
