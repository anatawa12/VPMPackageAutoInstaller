using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace Anatawa12.VpmPackageAutoInstaller.Creator
{
    public class VpmPackageAutoInstallerCreator : EditorWindow
    {
        [MenuItem("Window/VPMPackageAutoInstallerCreator")]
        public static void OpenGui()
        {
            GetWindow<VpmPackageAutoInstallerCreator>();
        }

        private Action _onProjectChange;

        private void OnEnable()
        {
            EditorApplication.projectChanged += _onProjectChange = OnProjectChange;
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= _onProjectChange;
        }

        private bool _shouldReload;
        [CanBeNull] private TextAsset _packageJsonAsset;
        [CanBeNull] private LoadedPackageInfo _loaded;

        private void OnGUI()
        {
            EditorGUILayout.LabelField("VPMPackageAutoInstaller Creator");

            var old = _packageJsonAsset;
            _packageJsonAsset =
                (TextAsset)EditorGUILayout.ObjectField("package.json", _packageJsonAsset, typeof(TextAsset), false);
            if (_packageJsonAsset != old || _shouldReload)
                LoadPackageInfos();
            _shouldReload = false;

            if (_loaded != null)
            {
                // TODO: allow creator to choose from ~, ^ and specific version descriptor.
                //var tag = EditorGUILayout.TextField("git tag", _tagName ?? _inferredGitInfo.tag ?? "");
                //if (tag != (_tagName ?? _inferredGitInfo.tag ?? ""))
                //    _tagName = tag;

                if (_loaded.PackagesWithoutRepository.Count != 0)
                {
                    var style = new GUIStyle(EditorStyles.label);
                    style.normal.textColor = Color.yellow;
                    EditorGUILayout.LabelField("Repository for the following packages Not Found", style);
                    foreach (var package in _loaded.PackagesWithoutRepository)
                    {
                        EditorGUILayout.LabelField($"{package}");
                    }
                }

                if (GUILayout.Button("Create Installer"))
                {
                    CreateInstaller(_loaded);
                }
            }
            else
            {
                if (_packageJsonAsset != null)
                {
                    EditorGUILayout.LabelField("The file is not valid package.json");
                }
            }
        }

        private static void CreateInstaller([NotNull] LoadedPackageInfo loaded)
        {
            try
            {
                var path = EditorUtility.SaveFilePanel("Create Installer...",
                    "",
                    (loaded.RootPackageJson.DisplayName ?? loaded.RootPackageJson.Name) + "-installer.unitypackage",
                    "unitypackage");
                if (string.IsNullOrEmpty(path)) return;

                var dependencies = new Dictionary<string, string>
                    { { loaded.RootPackageJson.Name, loaded.RootPackageJson.Version } };

                var legacyAssets = loaded.LegacyAssets.Count == 0
                    ? null
                    : loaded.LegacyAssets.ToDictionary(i => i.Path, i => i.GUID);

                var configJsonObj = new CreatorConfigJson(dependencies, loaded.Repositories.ToList(), legacyAssets);
                var configJson = JsonConvert.SerializeObject(configJsonObj);

                var created = PackageCreator.CreateUnityPackage(Encoding.UTF8.GetBytes(configJson));

                File.WriteAllBytes(path, created);
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception e)
            {
                Debug.LogError("ERROR creating installer. " +
                               "Please report me at https://github.com/anatawa12/VPMPackageAutoInstaller/issues/new " +
                               "with the next error message.");
                Debug.LogError(e);
                EditorUtility.DisplayDialog("ERROR",
                    "Internal Error occurred.\n" +
                    "If Possible, please report me at https://github.com/anatawa12/VPMPackageAutoInstaller/issues/new " +
                    "with error message on console.\n" +
                    "(there also be the link to report on error console.)", "OK");
            }
        }

        private void OnProjectChange()
        {
            _shouldReload = true;
        }

#region get / infer manifest info
        private void LoadPackageInfos()
        {
            if (_packageJsonAsset == null)
            {
                _loaded = null;
                return;
            }       
            _loaded = LoadPackageJsonRecursive(_packageJsonAsset);
        }


        private static LoadedPackageInfo LoadPackageJsonRecursive([NotNull] TextAsset packageJsonAsset)
        {
            var rootPackageJson = LoadPackageJson(packageJsonAsset);
            if (rootPackageJson == null)
            {
                return null;
            }

            var vrcPackages = VRChatPackageManager.VRChatPackages;
            var foundReposByPackage = VRChatPackageManager.CollectUserRepoForPackage();

            var legacyAssets = new HashSet<LegacyInfo>();

            var packagesWithoutRepository = new HashSet<string>();
            var repositories = new HashSet<string>();

            // will be asked
            var includedPackages = new HashSet<string>();
            var packageJsons = new Queue<PackageJson>();
            packageJsons.Enqueue(rootPackageJson);

            while (packageJsons.Count != 0)
            {
                var srcJson = packageJsons.Dequeue();
                var vpmDependencies = srcJson.VpmDependencies;
                if (vpmDependencies != null)
                {
                    foreach (var pair in vpmDependencies)
                    {
                        if (includedPackages.Contains(pair.Key)) continue;
                        var packageJson = AssetDatabase.LoadAssetAtPath<TextAsset>($"Packages/{pair.Key}/package.json");
                        if (packageJson == null) continue;
                        var json = LoadPackageJson(packageJson);
                        if (json == null) continue;

                        includedPackages.Add(pair.Key);
                        packageJsons.Enqueue(json);
                    }
                }

                if (vrcPackages.Contains(srcJson.Name))
                {
                    // official/cured package is not required to add repository manually
                }
                else if (srcJson.Repo != null)
                {
                    repositories.Add(srcJson.Repo);
                }
                else if (foundReposByPackage.TryGetValue(new PackageInfo(srcJson), out var repo))
                {
                    repositories.Add(repo.Url);
                }
                else
                {
                    // no repositories found: add warning
                    packagesWithoutRepository.Add(srcJson.Name);
                }

                LegacyInfo MakeLegacyInfo(KeyValuePair<string, string> pair) => new LegacyInfo(pair.Key, pair.Value);

                foreach (var info in srcJson.LegacyFolders?.Select(MakeLegacyInfo) ?? Enumerable.Empty<LegacyInfo>())
                    legacyAssets.Add(info);
                foreach (var info in srcJson.LegacyFiles?.Select(MakeLegacyInfo) ?? Enumerable.Empty<LegacyInfo>())
                    legacyAssets.Add(info);
            }

            return new LoadedPackageInfo(
                rootPackageJson: rootPackageJson,
                repositories: repositories,
                packagesWithoutRepository: packagesWithoutRepository,
                legacyAssets: legacyAssets);
        }

        [CanBeNull]
        private static PackageJson LoadPackageJson([NotNull] TextAsset asset)
        {
            try
            {
                PackageJson json = JsonConvert.DeserializeObject<PackageJson>(asset.text);
                if (json == null || json.Name == null || json.Version == null) return null;
                return json;
            }
            catch (JsonException e)
            {
                Debug.LogError(e);
                return null;
            }
        }
#endregion get / infer manifest info

    }

    internal class LoadedPackageInfo
    {
        public readonly PackageJson RootPackageJson;
        public readonly HashSet<string> Repositories;
        public readonly HashSet<string> PackagesWithoutRepository;
        public readonly HashSet<LegacyInfo> LegacyAssets;

        public LoadedPackageInfo(
            PackageJson rootPackageJson,
            HashSet<string> repositories,
            HashSet<string> packagesWithoutRepository,
            HashSet<LegacyInfo> legacyAssets
        )
        {
            RootPackageJson = rootPackageJson;
            Repositories = repositories;
            PackagesWithoutRepository = packagesWithoutRepository;
            LegacyAssets = legacyAssets;
        }
    }

    internal static class VRChatPackageManager
    {
        public static string GlobalFoler = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRChatCreatorCompanion");

        public static string GlobalSettingPath = Path.Combine(GlobalFoler, "settings.json");
        public static string GlobalReposFolder = Path.Combine(GlobalFoler, "Repos");


        public static HashSet<string> VRChatPackages = new HashSet<string>(
            from repoName in new[] { "vrc-curated.json", "vrc-official.json" }
            let path = Path.Combine(GlobalReposFolder, repoName)
            let text = TryReadAllText(path)
            where text != null
            let json = JsonConvert.DeserializeObject<VpmLocalRepository>(text)
            from name in json.Repo.Packages.Keys
            select name);

        public static Dictionary<PackageInfo, VpmUserRepo> CollectUserRepoForPackage()
        {
            var settings = JsonConvert.DeserializeObject<VpmGlobalSettingsJson>(File.ReadAllText(GlobalSettingPath));
            settings.UserRepos.Reverse();
            var enumerable =
                from userRepoInfo in settings.UserRepos
                let text = TryReadAllText(userRepoInfo.LocalPath)
                where text != null
                let json = JsonConvert.DeserializeObject<VpmLocalRepository>(text)
                from kvp in json.Repo.Packages
                let package = kvp.Key
                from version in kvp.Value.Packages.Keys
                select (pkg: new PackageInfo(package, version), userRepoInfo);
            return enumerable.ToDictionary(x => x.pkg, x => x.userRepoInfo);
        }

        private static string TryReadAllText(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    internal static class PackageCreator
    {
        private const string InstallerTemplateUnityPackageGuid = "f1f874df1c4e54463bdfd6d886007936";

        public static byte[] CreateUnityPackage(byte[] configJson)
        {
            var template = FindTemplate();
            var gzDecompressStream = new GZipStream(new MemoryStream(template, false), CompressionMode.Decompress);
            var uncompressed = new byte[GetGZipDecompressedSize(template)];
            for (int i = 0; i < uncompressed.Length; i++)
            {
                i += gzDecompressStream.Read(uncompressed, i, uncompressed.Length - i);
            }
            var tar = makeTarWithJson(uncompressed, configJson);
            var outStream = new MemoryStream();
            var gzCompressStream = new GZipStream(outStream, CompressionLevel.Optimal);
            gzCompressStream.Write(tar, 0, tar.Length);
            gzCompressStream.Dispose();
            return outStream.ToArray();
        }

        private static int GetGZipDecompressedSize(byte[] template)
        {
            var lenField = template.Length - 4;
            return template[lenField] 
                   | template[lenField + 1] << 8 
                   | template[lenField + 2] << 16
                   | template[lenField + 3] << 24;
        }

        private static byte[] FindTemplate()
        {
            return AssetDatabase
                .LoadAssetAtPath<TextAsset>(AssetDatabase.GUIDToAssetPath(InstallerTemplateUnityPackageGuid)).bytes;
        }

        #region javascript reimplementation
        // this part is re-implementation of creator.mjs.
        // When you edit this, you must check if I must also modify creator.mjs.

        // ReSharper disable InconsistentNaming
        private const int chunkLen = 512;
        private const int nameOff = 0;
        private const int nameLen = 100;
        private const int sizeOff = 124;
        private const int sizeLen = 12;
        private const int checksumOff = 148;
        private const int checksumLen = 8;
        private const string configJsonPathInTar = "./9028b92d14f444e2b8c389be130d573f/asset";

        static byte[] makeTarWithJson(byte[] template, byte[] json)
        {
            int cursor = 0;
            while (cursor < template.Length) {
                var size = readOctal(template, cursor + sizeOff, sizeLen);
                var contentSize = (size + chunkLen - 1) & ~(chunkLen - 1);
                var name = readString(template, cursor + nameOff, nameLen);
                if (name == configJsonPathInTar) {
                    // set new size and calc checksum
                    saveOctal(template, cursor + sizeOff, sizeLen, json.Length, sizeLen - 1);
                    Fill(template, (byte)' ', cursor + checksumOff, checksumLen);
                    var checksum = calcCheckSum(template, cursor, chunkLen);
                    saveOctal(template, cursor + checksumOff, checksumLen, checksum, checksumLen - 2);

                    // calc pad size
                    var padSize = json.Length % chunkLen == 0 ? 0 : (chunkLen - json.Length);

                    // create tar file
                    var result = new byte[cursor + chunkLen
                                          + json.Length + padSize
                                          + (template.Length - (cursor + chunkLen + contentSize))];
                    Array.Copy(template, 0, result, 0, cursor + chunkLen);
                    Array.Copy(json, 0, result, cursor + chunkLen, json.Length);
                    // there's no need to set padding because already 0-filled
                    Array.Copy(template, cursor + chunkLen + contentSize, 
                        result, (cursor + chunkLen) + json.Length + padSize,
                        template.Length - (cursor + chunkLen + contentSize));
                    return result;
                } else {
                    cursor += chunkLen;
                    cursor += contentSize;
                }
            }
            throw new InvalidOperationException("config.json not found");
        }

        /**
         * @param {Uint8Array} buf
         * @return {number}
         */
        static int calcCheckSum(byte[] buf, int offset, int length) {
            var sum = 0;
            for (var i = 0; i < length; i++) {
                sum = (sum + buf[offset + i]) & 0x1FFFF;
            }
            return sum;
        }

        static string readString(byte[] buf, int offset, int len) {
            var firstNullByte = Array.IndexOf(buf, (byte) 0, offset) - offset;
            if (firstNullByte < 0)
                return Encoding.UTF8.GetString(buf, offset, len);
            return Encoding.UTF8.GetString(buf, offset, firstNullByte);
        }
        
        static int readOctal(byte[] buf, int offset, int len)
        {
            var s = readString(buf, offset, len);
            if (s == "") return 0;
            return Convert.ToInt32(s, 8);
        }
        
        /**
         * @param {Uint8Array} buf
         * @param {number} offset
         * @param {number} len
         * @param {number} value
         * @param {number} octalLen
         */
        static void saveOctal(byte[] buf, int offset, int len, int value, int octalLen = 0) {
            var str = Convert.ToString(value, 8).PadLeft(octalLen, '0');
            var bytes = Encoding.UTF8.GetBytes(str);
            if (bytes.Length >= len)
                throw new IndexOutOfRangeException("space not enough");
            
            bytes.CopyTo(buf, offset);

            if (bytes.Length < len) {
                buf[offset + bytes.Length] = 0;
                for (var i = offset + bytes.Length + 1; i < len; i++)
                {
                    buf[offset + i] = (byte)' ';
                }
            }
        }
        
        private static void Fill(byte[] buffer, byte c, int offset, int length)
        {
            for (int i = 0; i < length; i++)
            {
                buffer[offset + i] = c;
            }
        }

        // ReSharper restore InconsistentNaming
        #endregion
    }

#pragma warning disable CS0649
    // ReSharper disable once ClassNeverInstantiated.Global
    internal class PackageJson
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("displayName")] public string DisplayName;
        [JsonProperty("version")] public string Version;
        [JsonProperty("vpmDependencies")] public Dictionary<string, string> VpmDependencies;
        [JsonProperty("repo")] public string Repo;

        [JsonProperty("legacyFolders"), CanBeNull]
        public Dictionary<string, string> LegacyFolders;

        [JsonProperty("legacyFiles"), CanBeNull]
        public Dictionary<string, string> LegacyFiles;
    }
#pragma warning restore CS0649

    internal struct PackageInfo
    {
        [NotNull] public readonly string Id;
        [NotNull] public readonly string Version;

        public PackageInfo(string id, string version)
        {
            Id = id;
            Version = version;
        }

        public PackageInfo(PackageJson packageJson) : this(packageJson.Name, packageJson.Version)
        {
        }

        public bool Equals(PackageInfo other) => Id == other.Id && Version == other.Version;
        public override bool Equals(object obj) => obj is PackageInfo other && Equals(other);
        public override int GetHashCode() => unchecked(Id.GetHashCode() * 397) ^ Version.GetHashCode();
    }

    internal struct LegacyInfo
    {
        [NotNull] public readonly string Path;
        [NotNull] public readonly string GUID;

        public LegacyInfo(string path, string guid)
        {
            Path = path;
            GUID = guid;
        }

        public bool Equals(LegacyInfo other) => Path == other.Path && GUID == other.GUID;
        public override bool Equals(object obj) => obj is LegacyInfo other && Equals(other);
        public override int GetHashCode() => unchecked((Path.GetHashCode() * 397) ^ GUID.GetHashCode());
    }

    internal class VpmPackage
    {
        [JsonProperty("versions")]
        // ReSharper disable once NotAccessedField.Global
        public Dictionary<string, PackageJson> Packages;
    }

    internal class VpmRepository
    {
        [JsonProperty("packages")]
        // ReSharper disable once NotAccessedField.Global
        public Dictionary<string, VpmPackage> Packages;
    }

    internal class VpmLocalRepository
    {
        [JsonProperty("repo")]
        // ReSharper disable once NotAccessedField.Global
        public VpmRepository Repo;
    }

    internal class VpmUserRepo
    {
        [JsonProperty("localPath")] public string LocalPath;
        [JsonProperty("url")] public string Url;
    }

    internal class VpmGlobalSettingsJson
    {
        [JsonProperty("userRepos")]
        // ReSharper disable once NotAccessedField.Global
        public List<VpmUserRepo> UserRepos;
    }

    internal class CreatorConfigJson
    {
        [JsonProperty("dependencies")]
        // ReSharper disable once NotAccessedField.Global
        public Dictionary<string, string> Dependencies;
        [JsonProperty("repositories")]
        // ReSharper disable once NotAccessedField.Global
        public List<string> Repositories;

        [JsonProperty("legacyAssets", NullValueHandling = NullValueHandling.Ignore)]
        // ReSharper disable once NotAccessedField.Global
        [CanBeNull] public Dictionary<string, string> LegacyAssets;

        public CreatorConfigJson(
            Dictionary<string, string> dependencies,
            List<string> repositories,
            [CanBeNull] Dictionary<string, string> legacyAssets
        )
        {
            Dependencies = dependencies;
            Repositories = repositories;
            LegacyAssets = legacyAssets;
        }
    }
}