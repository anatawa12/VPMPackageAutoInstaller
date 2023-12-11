VPMPackageAutoInstaller
===

An project to install [vpm] package published in non-official registry with unitypackage file.

This project is based on C# reimplementation of [vrc-get].

[vpm]: https://vcc.docs.vrchat.com/vpm/packages
[vrc-get]: https://github.com/anatawa12/vrc-get

## How to use

### Editor Plugin

This tool is also available on [Booth](https://anatawa12.booth.pm/items/4951120)!

1. download latest version of installer creator [here][download-creator-latest]
2. Import the unitypackage to the unity project contains ``package.json``
3. Open `VPMPackageAutoInstallerCreator` from Window menu
4. Select package.json
5. Click `Create Installer`

[download-creator-latest]: https://github.com/anatawa12/VPMPackageAutoInstaller/releases/latest/download/installer-creator.unitypackage

### Web tool

1. Open website [here][creator-web]
2. Write config.json
3. Click `create installer`

[creator-web]: https://anatawa12.github.io/VPMPackageAutoInstaller/

### API

At `https://api.anatawa12.com/create-vpai`, you can create VPAI installer unitypackage.
All parameters are passed through get param3eter

| name      | required | description                                                                                                               | example                                     |
|-----------|----------|---------------------------------------------------------------------------------------------------------------------------|---------------------------------------------|
| `name`    | no       | The name of unitypackage will be downloaded. `{}` will be replaced with version nane. (default: `installer.unitypackage`) | `AvatarOptimizer-{}-installer.unitypackage` |
| `repos[]` | no       | VPM repositories to be added. you can specify multiple repositories at once.                                              | `https://vpm.anatawa12.com/vpm.json`        |
| `repo`    | no       | Alias of `repos[]`                                                                                                        |                                             |
| `package` | yes      | Package Name (id) of package the installer is for                                                                         | `com.anatawa12.avatar-optimizer`            |
| `version` | yes      | Package version (id) of package the installer is for                                                                      | `0.2.x`                                     |

For example, https://api.anatawa12.com/create-vpai/?name=AvatarOptimizer-{}-installer.unitypackage&repo=https://vpm.anatawa12.com/vpm.json&package=com.anatawa12.avatar-optimizer&version=0.2.x
will make unitypackage for AvatarOptimizer 0.2.x

### Other ways

<details>
<summary>click to open other ways</summary>

#### CLI Tool

1. Download latest version of installer creator [here][download-creator-js-latest].
2. Create config.json
3. Run `node path/to/creator.mjs path/to/config.json path/to/output.unitypackage` or
    `deno run --allow-net --allow-read --allow-write path/to/creator.mjs path/to/config.json path/to/output.unitypackage`

[download-creator-js-latest]: https://github.com/anatawa12/VPMPackageAutoInstaller/releases/latest/download/creator.mjs

#### Just create unitypackage

1. Clone or [download][download-this] this project.
2. Execute `./Assets/InstallerSource/DllBuild\~/build-and-copy.sh` to build ths tool
3. Edit config.json at `Assets/com.anatawa12.vpm-package-auto-installer/config.json`
4. Open this project with Unity (if you did, there's no need to relaunch)
5. Left-click on `Assets/com.anatawa12.vpm-package-auto-installer` and click `Export Package`
6. un-check `Incliude dependencies` on `Exporting package` dialog.
7. Click `Export...` and save `.unitypackage` to anywhere you want.
8. Share `.unitypackage` with user who want to use your package.

</details>

[download-this]: https://github.com/anatawa12/VPMPackageAutoInstaller/archive/refs/heads/master.zip

## Config format

```json5
// in the config file, comment is not supported but for documentation, comment is used here.
{
  // list of vpm repositories to be added
  // You should list up required vpm repositories for vpmDependencies and their vpmDependencies
  // NOTICE: You should not include vrchat official or curated repositories. 
  //         official / curated repositories are always included in repositories
  "vpmRepositories": [
    "https://vpm.anatawa12.com/vpm.json",
    // or you can use object form to define headers for the repository.
    {
      "url": "https://vpm.anatawa12.com/vpm.json",
      "headers": {
        "x-your-header": "your-header-value-here"
      }
    }
  ],
  // List of dependencies to be added. Non-vpm dependencies are not supported.
  "vpmDependencies": {
    // you can use any form of version range supported by VPM such as `^0.1.2`, `~0.1.2`, or `>=0.1.2`
    "com.anatawa12.custom-localization-for-editor-extension": "^0.2.0"
  },
  // by default, beta releases are not allowed.
  // to allow all beta versions in that range, please make this true
  "includePrerelease": false,
  // If you want to disallow unity version lower than specific version, you can specify it here.
  // Regardless if this is not specified, unity version will be checked by VPM.
  "minimumUnity": "2022.3"
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
  |    `- com.anatawa12.vpm-package-auto-installer.dll (gitignored) - the compiled dll file
  |
  +- InstallerSource - the folder for sorurce code of dll file
  |    +- DllBuild~ - the dotnet sln for build `com.anatawa12.vpm-package-auto-installer.dll`
  |    |   +- build-and-copy.sh - the shellscript to build `com.anatawa12.vpm-package-auto-installer.dll`. there's nothing complicated in this script.
  |    |   |
  |    |   +- Directory.Build.props - global build settings
  |    |   +- VpmPackgeAtoInstallerPrecompiled.sln - the dotnet solution files
  |    |   |
  |    |   +- com.anatawa12.vpm-package-auto-installer.csproj - csproj to build VPAI
  |    |   |
  |    |   +- UnityEditor.csproj
  |    |   +- UnityEditor.Header.cs
  |    |   +- UnityEngine.csproj
  |    |   `- UnityEngine.Header.cs - the fake module of UnityEngine/Editor to build VPMPackageAutoInstaller
  |    |
  |    +- SimpleJson.cs - symlink to SimpleJson~/SimpleJson.cs
  |    +- SimpleJson~ - git submodule https://github.com/anatawa12/SimpleJson
  |    |
  |    +- semver.net - symlink to semver.net~/src/SemanticVersioning
  |    +- semver.net~ git submodule https://github.com/adamreeve/semver.net
  |    |
  |    +- VrcGetCs - C# reimplementation of https://github.com/anatawa12/vrc-get. see README.
  |    |
  |    +- com.anatawa12.vpm-package-auto-installer.source.asmdef - the asmdef for build-time check
  |    +- BurstPatcch.cs - The patch for burst compiler warnings
  |    |
  |    `- VpmPackageAutoInstaller.cs - the main module of VPAI
  |
  `- Tester - the module to test vpm package auto installer. This module includes unit tests
```

To test this package, you need to run `Assets/InstallerSource/build-and-copy.sh` to compile `com.anatawa12.vpm-package-auto-installer.dll`.
