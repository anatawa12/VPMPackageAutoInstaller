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
