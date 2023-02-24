VPMPackageAutoInstaller
===

An experimental project to install [vpm] package published in non-official registry with unitypackage file.

[vpm]: https://vcc.docs.vrchat.com/vpm/packages

## How to use

### Editor Plugin

1. download latest version of installer creator [here][download-creator-latest]
2. Import the unitypackage to the unity project contains ``package.json``
3. Open `VPMPackageAutoInstallerCreator` from Window menu
4. Select package.json
5. Click `Create Installer`

[download-creator-latest]: https://github.com/anatawa12/VPMPackageAutoInstaller/releases/latest/download/installer-creator.unitypackage

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

[creator-web]: https://anatawa12.github.io/VPMPackageAutoInstaller/

[download-creator-js-latest]: https://github.com/anatawa12/VPMPackageAutoInstaller/releases/latest/download/creator.mjs

#### Just create unitypackage

1. Clone or [download][download-this] this project.
2. Edit config.json at `Assets/com.anatawa12.vpm-package-auto-installer/config.json`
3. Open this project with Unity (if you did, there's no need to relaunch)
4. Select `Assets/com.anatawa12.vpm-package-auto-installer` in Unity's File view
5. Left-click on `Assets/com.anatawa12.vpm-package-auto-installer` and click `Export Package`
6. un-check `Incliude dependencies` on `Exporting package` dialog.
7. Click `Export...` and save `.unitypackage` to anywhere you want.
8. Share `.unitypackage` with user who want to use your package.

</details>

[download-this]: https://github.com/anatawa12/VPMPackageAutoInstaller/archive/refs/heads/master.zip

## Config format

```json
// in the config file, comment is not supported but for documentation, comment is used here.
{
  // list of vpm repositories to be added
  // You should list up required vpm repositories for vpmDependencies and their vpmDependencies
  // NOTICE: You should not include vrchat official or curated repositories
  "vpmRepositories": [
    "https://vpm.anatawa12.com/vpm.json"
  ],
  // List of dependencies to be added. Non-vpm dependencies are not supported.
  "vpmDependencies": {
    // you may use 'x.y.z', '^x.y.z', or '~x.y.z'
    "com.anatawa12.custom-localization-for-editor-extension": "^0.2.0"
  },
  // List of folders or files will be removed just before installing the packages above
  // just like legacyFolders / legacyFiles in VPM manifest
  "legacyAssets": {
    // you may use / or \\ for asset path
    "Assets/path/of/asset": "<guid-of-asset-here>",
    "Assets\\path\\of\\asset": "<guid-of-asset-here>"
  }
}
```

## How this works

This uses `InitializeOnLoad` attribute to run some script on unpacked `unitypackage` and
on `InitializeOnLoad`, modifies `vpm-manifest.json` based on `config.json` and trigger VPM. 
Just after modification, this package deletes files & folders of this project based on `GUID`.
GUID of C#, `config.json`, and `com.anatawa12.vpm-package-auto-installer` are hard-coded.

## Development

To make dll distribution, this project is a little complicated.
Here's overview of the files.

```
Assets
  +- com.anatawa12.vpm-package-auto-installer - the folder for unitypackage
  |    +- config.json - the sample config file
  |    `- VPMPackageAutoInstaller.dll (gitignored) - the compiled dll file
  |
  +- InstallerSource - the folder for sorurce code of dll file
  |    +- DllBuild~ - the dotnet sln for build `VPMPackageAutoInstaller.dll`
  |    |   +- build-and-copy.sh - the shellscript to build `VPMPackageAutoInstaller.dll`. there's nothing complicated in this script.
  |    |   |
  |    |   +- Directory.Build.props
  |    |   +- UnityEditor.csproj
  |    |   +- UnityEngine.csproj
  |    |   +- UVPMPackageAutoInstaller.csproj
  |    |   +- VpmPackgeAtoInstallerPrecompiled.csproj - the dotnet solution files
  |    |   |
  |    |   +- UnityEditor.Header.cs
  |    |   `- UnityEngine.Header.cs - the fake module of UnityEngine/Editor to build `VPMPackageAutoInstaller.dll`
  |    +- com.anatawa12.vpm-package-auto-installer.asmdef - the asmdef for build-time check
  |    `- VpmPackageAutoInstaller.cs - the main cs file
  |
  `- Tester - the module to test vpm package auto installer. This module includes unit tests
```

To test this package, you need to run `Assets/InstallerSource/build-and-copy.sh` to compile `VPMPackageAutoInstaller.dll`.
