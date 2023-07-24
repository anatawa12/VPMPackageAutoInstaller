using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using CompressionLevel = System.IO.Compression.CompressionLevel;

namespace Anatawa12.VpmPackageAutoInstaller.Creator
{
    internal class VpmPackageAutoInstallerCreator : EditorWindow
    {
        [MenuItem("Tools/VPMPackageAutoInstaller Creator")]
        public static void OpenGui() => GetWindow<VpmPackageAutoInstallerCreator>("VPMPackageAutoInstaller Creator");

        private Action _onProjectChange;

        private bool _shouldReload;
        [CanBeNull] private TextAsset _packageJsonAsset;
        private SerializedObject _serialized;
        private SerializedProperty _packages, _repositories;
        public GuiPackageInfo[] packages = Array.Empty<GuiPackageInfo>();
        public GuiRepositoryInfo[] repositories = Array.Empty<GuiRepositoryInfo>();
        public string[] errors = Array.Empty<string>();
        public string[] warnings = Array.Empty<string>();
        public Vector2 scroll;

        private void OnEnable()
        {
            EditorApplication.projectChanged += _onProjectChange = OnProjectChange;
            _serialized = new SerializedObject(this);
            _packages = _serialized.FindProperty(nameof(packages));
            _repositories = _serialized.FindProperty(nameof(repositories));
            ComputeErrors();
        }

        private void OnDisable()
        {
            EditorApplication.projectChanged -= _onProjectChange;
        }

        [Serializable]
        internal struct GuiPackageInfo
        {
            public string id;
            public string version;

            public GuiPackageInfo(string id, string version) => (this.id, this.version) = (id, version);
        }

        [Serializable]
        public class GuiRepositoryInfo
        {
            public string url;
            public HeaderInfo[] headers;

            public GuiRepositoryInfo(string url) => this.url = url;
        }

        [Serializable]
        public class HeaderInfo
        {
            public string name;
            public string value;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("VPMPackageAutoInstaller Creator");

            _packageJsonAsset =
                (TextAsset)EditorGUILayout.ObjectField("package.json", _packageJsonAsset, typeof(TextAsset), false);
            EditorGUI.BeginDisabledGroup(!_packageJsonAsset);
            if (GUILayout.Button("Load from package.json") && _packageJsonAsset)
                LoadPackageJsonRecursive(_packageJsonAsset);
            EditorGUI.EndDisabledGroup();

            _serialized.Update();

            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();

            EditorGUILayout.LabelField("Packages", EditorStyles.boldLabel);
            {
                var (labelRect, valueRect, _) = SplitRectToTwoAndButton(EditorGUILayout.GetControlRect());
                GUI.Label(labelRect, "Package Id");
                GUI.Label(valueRect, "Package Version Range");
            }
            for (var i = 0; i < _packages.arraySize; i++)
            {
                var package = _packages.GetArrayElementAtIndex(i);
                var packageId = package.FindPropertyRelative(nameof(GuiPackageInfo.id));
                var packageVersion = package.FindPropertyRelative(nameof(GuiPackageInfo.version));

                var (labelRect, valueRect, buttonRect) = SplitRectToTwoAndButton(EditorGUILayout.GetControlRect());
                var indentLevelOld = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                EditorGUI.PropertyField(labelRect, packageId, GUIContent.none);
                EditorGUI.PropertyField(valueRect, packageVersion, GUIContent.none);
                if (GUI.Button(buttonRect, "x"))
                    _packages.DeleteArrayElementAtIndex(i);
                EditorGUI.indentLevel = indentLevelOld;
            }
            if (GUI.Button(EditorGUI.IndentedRect(EditorGUILayout.GetControlRect()), "Add Package"))
                _packages.arraySize++;

            EditorGUILayout.Space();

            EditorGUILayout.LabelField("Repositories", EditorStyles.boldLabel);
            for (var i = 0; i < _repositories.arraySize; i++)
                DrawRepository(_repositories.GetArrayElementAtIndex(i),
                    () => _repositories.DeleteArrayElementAtIndex(i));
            if (GUI.Button(EditorGUI.IndentedRect(EditorGUILayout.GetControlRect()), "Add Repository"))
                _repositories.arraySize++;

            _serialized.ApplyModifiedProperties();

            EditorGUILayout.Space();
            if (EditorGUI.EndChangeCheck())
                ComputeErrors();

            EditorGUI.BeginDisabledGroup(errors.Length != 0);
            if (GUI.Button(EditorGUI.IndentedRect(EditorGUILayout.GetControlRect()), "Create Installer UnityPackage"))
                CreateInstaller();
            EditorGUI.EndDisabledGroup();

            scroll = EditorGUILayout.BeginScrollView(scroll);

            foreach (var error in errors)
                EditorGUILayout.HelpBox(error, MessageType.Error);

            if (warnings != null && warnings.Length != 0)
            {
                foreach (var warning in warnings)
                    EditorGUILayout.HelpBox(warning, MessageType.Warning);
                if (GUILayout.Button("Clear Warnings"))
                    warnings = Array.Empty<string>();
            }
            EditorGUILayout.EndScrollView();
        }

        private void ComputeErrors()
        {
            // ReSharper disable once LocalVariableHidesMember
            var errors = new List<string>();

            errors.AddRange(from duplicatedName in FindDuplicates(packages.Select(x => x.id))
            select $"There are two {duplicatedName}");

            errors.AddRange(from package in packages
                where !IsValidVersionName(package.version)
                select $"Version range for {package.id} is not valid");

            if (packages.Length == 0)
                errors.Add("There is no packages");

            if (packages.Any(x => x.id == ""))
                errors.Add("There is package with empty id");

            errors.AddRange(from url in FindDuplicates(repositories.Select(x => x.url))
                select $"There are two repository with '{url}'");

            errors.AddRange(from repository in repositories
                from header in FindDuplicates(repository.headers.Select(x => x.name.ToLowerInvariant()))
                select $"There are two or more header named '{header}' for '{repository.url}'.");

            errors.AddRange(from repository in repositories
                where !IsValidRepositoryUrl(repository.url)
                select $"Invalid url: '{repository.url}'");

            if (repositories.Length == 0)
            {
                if (packages.Length == 0 || packages.All(x => !VRChatPackageManager.VRChatPackages.Contains(x.id)))
                    errors.Add("No repository specified");
            }

            this.errors = errors.ToArray();
        }

        private static bool IsValidRepositoryUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme.ToLowerInvariant() != "http" && uri.Scheme.ToLowerInvariant() != "https") return false;
            if (uri.Host == "") return false;
            return true;
        }

        private static HashSet<string> FindDuplicates(IEnumerable<string> enumerable)
        {
            var packageNames = new HashSet<string>();
            var duplicatedNames = new HashSet<string>();

            foreach (var package in enumerable)
            {
                if (!packageNames.Add(package))
                    duplicatedNames.Add(package);
            }

            return duplicatedNames;
        }

        private bool IsValidVersionName(string range) =>
            !string.IsNullOrWhiteSpace(range) && SemanticVersioning.Range.TryParse(range, out _);

        static (Rect, Rect, Rect) SplitRectToTwoAndButton(Rect position)
        {
            var indent = EditorGUI.indentLevel * 15f;
            var buttonWidth = EditorGUIUtility.singleLineHeight;

            var labelWidth = (position.width - indent - 2.0f - 2.0f - buttonWidth) / 2;
            var labelRect = new Rect(position.x + indent, position.y, labelWidth, 18);
            var valueRect = new Rect(position.x + labelWidth + indent + 2.0f, position.y,
                position.width - labelWidth - indent - 2.0f - 2.0f - buttonWidth, position.height);
            var buttonRect = new Rect(position.xMax - buttonWidth, position.y, buttonWidth, 18);

            return (labelRect, valueRect, buttonRect);
        }

        private static void DrawRepository(SerializedProperty property, [CanBeNull] Action remove = null)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (GUI.Button(EditorGUI.IndentedRect(EditorGUILayout.GetControlRect()), "Remove This Repository"))
            {
                remove?.Invoke();
                return;
            }

            EditorGUILayout.PropertyField(property.FindPropertyRelative(nameof(GuiRepositoryInfo.url)));

            var headersProp = property.FindPropertyRelative(nameof(GuiRepositoryInfo.headers));
            EditorGUILayout.LabelField("Headers", EditorStyles.boldLabel);

            {
                var (labelRect, valueRect, _) = SplitRectToTwoAndButton(EditorGUILayout.GetControlRect());
                GUI.Label(labelRect, "Header Name");
                GUI.Label(valueRect, "Value");
            }

            for (var i = 0; i < headersProp.arraySize; i++)
            {
                var element = headersProp.GetArrayElementAtIndex(i);
                var name = element.FindPropertyRelative(nameof(HeaderInfo.name));
                var value = element.FindPropertyRelative(nameof(HeaderInfo.value));

                var (labelRect, valueRect, buttonRect) = SplitRectToTwoAndButton(EditorGUILayout.GetControlRect());
                var indentLevelOld = EditorGUI.indentLevel;
                EditorGUI.indentLevel = 0;
                EditorGUI.PropertyField(labelRect, name, GUIContent.none);
                EditorGUI.PropertyField(valueRect, value, GUIContent.none);
                if (GUI.Button(buttonRect, "x"))
                    headersProp.DeleteArrayElementAtIndex(i);
                EditorGUI.indentLevel = indentLevelOld;
            }

            if (GUI.Button(EditorGUI.IndentedRect(EditorGUILayout.GetControlRect()), "Add Header"))
                headersProp.arraySize++;

            EditorGUILayout.EndVertical();
        }

        private void CreateInstaller()
        {
            try
            {
                var path = EditorUtility.SaveFilePanel("Create Installer...",
                    "",
                    packages[0].id + "-installer.unitypackage",
                    "unitypackage");
                if (string.IsNullOrEmpty(path)) return;

                var dependencies = packages.ToDictionary(x => x.id, x => x.version);

                var configJsonObj = new CreatorConfigJson(dependencies, 
                    repositories.Select(x => new ConfigRepositoryInfo(x.url,
                        x.headers.ToDictionary(y => y.name, y => y.value))).ToList());
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

        private void LoadPackageJsonRecursive([NotNull] TextAsset packageJsonAsset)
        {
            var rootPackageJson = LoadPackageJson(packageJsonAsset);
            if (rootPackageJson == null)
            {
                warnings = new []{"Cannot read the package.json. The package.json is not valid."};
                return;
            }

            var vrcPackages = VRChatPackageManager.VRChatPackages;
            var (foundReposByPackage, foundReposByPackageName) = VRChatPackageManager.CollectUserRepoForPackage();

            var packagesWithoutRepository = new HashSet<string>();
            var repositoryUrls = new HashSet<string>();

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
                    repositoryUrls.Add(srcJson.Repo);
                }
                else if (foundReposByPackage.TryGetValue(new PackageInfo(srcJson), out var repo))
                {
                    repositoryUrls.Add(repo.Url);
                }
                else if (foundReposByPackageName.TryGetValue(srcJson.Name, out repo))
                {
                    repositoryUrls.Add(repo.Url);
                }
                else
                {
                    // no repositories found: add warning
                    packagesWithoutRepository.Add(srcJson.Name);
                }
            }

            packages = new[] { new GuiPackageInfo(rootPackageJson.Name, rootPackageJson.Version) };
            repositories = repositoryUrls.Select(x => new GuiRepositoryInfo(x)).ToArray();

            warnings = Array.Empty<string>();

            if (packagesWithoutRepository.Count != 0)
            {
                var messageBuilder = new StringBuilder("Repository for the following packages not found: ");
                foreach (var pkg in packagesWithoutRepository)
                    messageBuilder.Append('\n').Append(pkg);
                ArrayUtility.Add(ref warnings, messageBuilder.ToString());
            }
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

        public static (Dictionary<PackageInfo, VpmUserRepo>, Dictionary<string, VpmUserRepo>) CollectUserRepoForPackage()
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
            var byInfo = new Dictionary<PackageInfo, VpmUserRepo>();
            var byName = new Dictionary<string, VpmUserRepo>();
            foreach (var (pkg, userRepoInfo) in enumerable)
            {
                if (!byInfo.ContainsKey(pkg))
                    byInfo.Add(pkg, userRepoInfo);
                if (!byName.ContainsKey(pkg.Id))
                    byName.Add(pkg.Id, userRepoInfo);
            }
            return (byInfo, byName);
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
        public List<ConfigRepositoryInfo> Repositories;

        public CreatorConfigJson(
            Dictionary<string, string> dependencies,
            List<ConfigRepositoryInfo> repositories
        )
        {
            Dependencies = dependencies;
            Repositories = repositories;
        }
    }

    [JsonConverter(typeof(JsonConverter))]
    internal class ConfigRepositoryInfo
    {
        // ReSharper disable once NotAccessedField.Global
        [JsonProperty("url")]
        public string Url;
        // ReSharper disable once NotAccessedField.Global
        [JsonProperty("headers")]
        public Dictionary<string, string> Headers;

        public ConfigRepositoryInfo()
        {
        }

        public ConfigRepositoryInfo(string url) => Url = url;

        public ConfigRepositoryInfo(string url, Dictionary<string, string> headers) => (Url, Headers) = (url, headers);

        public class JsonConverter : JsonConverter<ConfigRepositoryInfo>
        {
            public override ConfigRepositoryInfo ReadJson(JsonReader reader, Type objectType, ConfigRepositoryInfo existingValue,
                bool hasExistingValue,
                JsonSerializer serializer)
            {
                var token = JToken.Load(reader);
                return token.Type == JTokenType.String ? new ConfigRepositoryInfo(token.ToString()) : token.ToObject<ConfigRepositoryInfo>(serializer);
            }

            public override void WriteJson(JsonWriter writer, ConfigRepositoryInfo value, JsonSerializer serializer)
            {
                if (value.Headers == null || value.Headers.Count == 0)
                {
                    serializer.Serialize(writer, value.Url);
                }
                else
                {
                    serializer.Serialize(writer, value);
                }
            }
        }
    }
}
