using System;
using System.Collections;
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

namespace Anatawa12.AutoPackageInstaller.Creator
{
    public class AutoPackageInstallerCreator : EditorWindow
    {
        [MenuItem("Window/AutoPackageInstallerCreator")]
        public static void OpenGui()
        {
            GetWindow<AutoPackageInstallerCreator>();
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
        private TextAsset _packageJsonAsset;
        private PackageJson _rootPackageJson;
        private ManifestJson _manifestJson;
        private List<PackageInfo> _packages;
        private HashSet<LegacyInfo> _legacyAssets;
        private string _gitRemoteURL;
        private string _tagName;
        private (string remote, string tag) _inferredGitInfo;

        private void OnGUI()
        {
            EditorGUILayout.LabelField("AutoPackageInstaller Creator");

            var old = _packageJsonAsset;
            _packageJsonAsset =
                (TextAsset)EditorGUILayout.ObjectField("package.json", _packageJsonAsset, typeof(TextAsset), false);
            if (_packageJsonAsset != old || _shouldReload)
                LoadPackageInfos();
            _shouldReload = false;

            if (_packages != null)
            {
                var url = EditorGUILayout.TextField("git url", _gitRemoteURL ?? _inferredGitInfo.remote ?? "");
                if (url != (_gitRemoteURL ?? _inferredGitInfo.remote ?? ""))
                    _gitRemoteURL = url;
                
                var tag = EditorGUILayout.TextField("git tag", _tagName ?? _inferredGitInfo.tag ?? "");
                if (tag != (_tagName ?? _inferredGitInfo.tag ?? ""))
                    _tagName = tag;

                if (_packages.Count != 0)
                {
                    EditorGUILayout.LabelField("The following packages will also be installed");
                    foreach (var package in _packages)
                    {
                        EditorGUILayout.LabelField($"{package.Id}: {package.GitURL}");
                    }
                }

                if (GUILayout.Button("Create Installer"))
                {
                    CreateInstaller();
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

        private void CreateInstaller()
        {
            try
            {

                var remoteUrl = _gitRemoteURL ?? _inferredGitInfo.remote ?? "";
                if (string.IsNullOrEmpty(remoteUrl))
                {
                    EditorUtility.DisplayDialog("ERROR", "Please set git remote url", "OK");
                    return;
                }

                var remoteTag = _tagName ?? _inferredGitInfo.tag ?? "";
                if (string.IsNullOrEmpty(remoteTag))
                {
                    if (!EditorUtility.DisplayDialog("WARNING", "version tag not specified. " +
                                                                "this will make installer for LATEST version. " +
                                                                "Are you sure?", "OK", "NO"))
                        return;
                }

                if (!string.IsNullOrEmpty(remoteTag))
                    remoteUrl = $"{remoteUrl}#{remoteTag}";

                var path = EditorUtility.SaveFilePanel("Create Installer...",
                    "",
                    (_rootPackageJson.DisplayName ?? _rootPackageJson.Name) + "-installer.unitypackage",
                    "unitypackage");
                if (string.IsNullOrEmpty(path)) return;

                var dependencies = new Dictionary<string, string>();
                foreach (var package in _packages)
                {
                    dependencies[package.Id] = package.GitURL;
                }

                dependencies[_rootPackageJson.Name] = remoteUrl;

                var legacyAssets = _legacyAssets.Count == 0 ? null : _legacyAssets.ToDictionary(i => i.Path, i => i.GUID);

                var configJsonObj = new CreatorConfigJson(dependencies, legacyAssets);
                var configJson = JsonConvert.SerializeObject(configJsonObj);

                var created = PackageCreator.CreateUnityPackage(Encoding.UTF8.GetBytes(configJson));

                File.WriteAllBytes(path, created);
                EditorUtility.RevealInFinder(path);
            }
            catch (Exception e)
            {
                Debug.LogError("ERROR creating installer. " +
                               "Please report me at https://github.com/anatawa12/AutoPackageInstaller/issues/new " +
                               "with the next error message.");
                Debug.LogError(e);
                EditorUtility.DisplayDialog("ERROR",
                    "Internal Error occurred.\n" +
                    "If Possible, please report me at https://github.com/anatawa12/AutoPackageInstaller/issues/new " +
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
                _packages = null;
                _legacyAssets = null;
                return;
            }       
            LoadPackageJsonRecursive();
            if (_packages == null) return;
            InferRootPackageGitUrl();
        }

        private void InferRootPackageGitUrl()
        {
            var packageJsonPath = AssetDatabase.GetAssetPath(_packageJsonAsset);
            if (packageJsonPath == null || !File.Exists(packageJsonPath)) return;
            packageJsonPath = Path.GetFullPath(packageJsonPath);
            var packageDir = Path.GetDirectoryName(packageJsonPath);

            var directoryName = packageDir;
            while (!string.IsNullOrEmpty(directoryName))
            {
                var gitDir = Path.Combine(directoryName, ".git");
                if (Directory.Exists(gitDir))
                {
                    var newInferred = TryParseGitRepo(gitDir, _rootPackageJson.Version);
                    var inRepoPath = packageDir.Substring(directoryName.Length + 1);
                    if (!string.IsNullOrEmpty(inRepoPath))
                    {
                        newInferred.remote += "?path=" + UrlEncode(inRepoPath.Replace('\\', '/'));
                    }

                    if (newInferred.remote != null && _inferredGitInfo.remote == _gitRemoteURL ||
                        string.IsNullOrEmpty(_gitRemoteURL))
                        _gitRemoteURL = null;
                    if (newInferred.tag != null && _inferredGitInfo.tag == _tagName ||
                        string.IsNullOrEmpty(_tagName))
                        _tagName = null;
                    _inferredGitInfo = newInferred;
                    return;
                }

                directoryName = Path.GetDirectoryName(directoryName);
            }

            _inferredGitInfo = (null, null);
        }

        private static class UrlNonEncodedCharTable
        {
            public static BitArray bits = new BitArray(128);

            static UrlNonEncodedCharTable()
            {
                // most codepoints between ' ' and '~' are non-encoded
                for (int i = ' '; i <= '~'; i++)
                    bits[i] = true;
                // query percent-encode set
                bits[' '] = false;
                bits['"'] = false;
                bits['#'] = false;
                bits['<'] = false;
                bits['>'] = false;
                bits['?'] = false;
                bits['`'] = false;
                bits['{'] = false;
                bits['}'] = false;
            }
        }

        private static string UrlEncode(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var builder = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                if (b < 128 && UrlNonEncodedCharTable.bits[b])
                    builder.Append((char)b);
                else
                    builder.Append('%').Append($"{b:x2}");
            }

            return builder.ToString();
        }

        private (string remote, string tag) TryParseGitRepo(string gitDir, string currentVersion)
        {
            try
            {
                var remote = GetRemoteUrlFromGitConfig(gitDir);
                if (remote == null) return (null, null);
                var tag = GetTagNameForCurrentVersion(gitDir, currentVersion);
                return (remote, tag);
            }
            catch (IOException)
            {
                return (null, null);
            }
        }

        private string GetRemoteUrlFromGitConfig(string gitDir)
        {
            string ParseGitEscape(string literal)
            {
                if (literal.Length == 0) return null;
                var builder = new StringBuilder(literal.Length);
                var i = 0;
                var quoted = false;

                if (literal[i] == '"')
                {
                    i++;
                    quoted = true;
                }

                i--;
                while (++i < literal.Length)
                {
                    if (literal[i] == '"')
                    {
                        if (quoted && i + 1 == literal.Length)
                            return builder.ToString();
                        return null;
                    }
                    else if (literal[i] == '\\')
                    {
                        if (++i >= literal.Length) break;
                        switch (literal[i])
                        {
                            case '"':
                            case '\\':
                                builder.Append(literal[i]);
                                break;
                            case 'n':
                                builder.Append('\n');
                                break;
                            case 'r':
                                builder.Append('\r');
                                break;
                            case 'b':
                                builder.Append('\b');
                                break;
                        }
                    }
                    else
                    {
                        builder.Append(literal[i]);
                    }
                }

                if (quoted) return null;
                return builder.ToString();
            }

            var lines = File.ReadAllLines(Path.Combine(gitDir, "config"));
            string currentRemoteName = null;
            string urlCandidate = null;
            foreach (var line in lines.Select(s => s.Trim()))
            {
                if (line.StartsWith("[remote \""))
                {
                    currentRemoteName = ParseGitEscape(line.Substring(
                        "[remote ".Length, line.Length - "[remote ".Length - "]".Length));
                }
                else if (line.StartsWith("["))
                {
                    currentRemoteName = null;
                }
                else
                {
                    if (currentRemoteName == null) continue;

                    var pair = line.Split(new[] { '=' }, 2);
                    if (pair[0].Trim() != "url" || pair.Length != 2) continue;
                    Debug.Log($"url for remote section {currentRemoteName} found");

                    if (currentRemoteName == "origin")
                    {
                        return ParseGitEscape(pair[1].Trim());
                    }

                    if (urlCandidate == null)
                    {
                        urlCandidate = ParseGitEscape(pair[1].Trim());
                    }
                }
            }

            return urlCandidate;
        }

        private string GetTagNameForCurrentVersion(string gitDir, string currentVersion)
        {
            var tagsDirPath = Path.Combine(gitDir, "refs", "tags");
            List<string> tags = Directory.GetFiles(tagsDirPath)
                .Where(tag => File.Exists(Path.Combine(tagsDirPath, tag)))
                .ToList();

            // first, find tag by name
            return tags.Where(tag =>
            {
                var versionIndex = tag.IndexOf(currentVersion, StringComparison.Ordinal);
                return versionIndex != -1
                       && (versionIndex == 0 || !char.IsDigit(tag[versionIndex - 1]))
                       && (versionIndex + currentVersion.Length == tag.Length ||
                           !char.IsDigit(tag[versionIndex + currentVersion.Length]));
            }).SingleOrDefault();
        }

        private void LoadPackageJsonRecursive()
        {
            _rootPackageJson = LoadPackageJson(_packageJsonAsset);
            if (_rootPackageJson == null)
            {
                _packages = null;
                _legacyAssets = null;
                return;
            }

            var versions = new Dictionary<string, string>();
            var order = new HashSet<string>();
            var packageJsons = new Queue<PackageJson>();
            var legacyAssets = new HashSet<LegacyInfo>();

            // will be asked
            packageJsons.Enqueue(_rootPackageJson);
            versions[_rootPackageJson.Name] = "DUMMY";

            while (packageJsons.Count != 0)
            {
                var srcJson = packageJsons.Dequeue();
                var gitDependencies

                 = srcJson.GitDependencies;
                if (gitDependencies != null)
                {
                    foreach (var pair in gitDependencies)
                    {
                        if (versions.TryGetValue(pair.Key, out var existing))
                        {
                            if (existing != pair.Value)
                            {
                                versions[pair.Key] = GetInstalledVersion(pair.Key) ?? pair.Value;
                            }

                            continue;
                        }

                        versions[pair.Key] = pair.Value;

                        var packageJson = AssetDatabase.LoadAssetAtPath<TextAsset>($"Packages/{pair.Key}/package.json");
                        order.Add(pair.Key);
                        if (packageJson == null) continue;
                        var json = LoadPackageJson(packageJson);
                        if (json != null)
                            packageJsons.Enqueue(json);
                    }
                }

                if (srcJson.LegacyFolders != null)
                {
                    foreach (var pathGuidPair in srcJson.LegacyFolders)
                    {
                        legacyAssets.Add(new LegacyInfo(pathGuidPair.Key, pathGuidPair.Value));
                    }
                }

                if (srcJson.LegacyFiles != null)
                {
                    foreach (var pathGuidPair in srcJson.LegacyFiles)
                    {
                        legacyAssets.Add(new LegacyInfo(pathGuidPair.Key, pathGuidPair.Value));
                    }
                }
            }

            _packages = order.Select(id => new PackageInfo(id, versions[id])).ToList();
            _legacyAssets = legacyAssets;
        }

        private string GetInstalledVersion(string pkgId)
        {
            if (_manifestJson == null)
                _manifestJson = JsonConvert.DeserializeObject<ManifestJson>(
                    File.ReadAllText("Packages/manifest.json", Encoding.UTF8));
            return _manifestJson?.Dependencies?[pkgId];
        }

        [CanBeNull]
        private PackageJson LoadPackageJson([NotNull] TextAsset asset)
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
        [JsonProperty("dependencies")] public Dictionary<string, string> Dependencies;
        [JsonProperty("gitDependencies")] public Dictionary<string, string> GitDependencies;

        [JsonProperty("legacyFolders"), CanBeNull]
        public Dictionary<string, string> LegacyFolders;

        [JsonProperty("legacyFiles"), CanBeNull]
        public Dictionary<string, string> LegacyFiles;
    }

    // ReSharper disable once ClassNeverInstantiated.Global
    internal class ManifestJson
    {
        [JsonProperty("dependencies")]
        // ReSharper disable once CollectionNeverUpdated.Global
        public Dictionary<string, string> Dependencies;
    }
#pragma warning restore CS0649

    internal class PackageInfo
    {
        public readonly string Id;
        public readonly string GitURL;

        public PackageInfo(string id, string gitURL)
        {
            Id = id;
            GitURL = gitURL;
        }
    }

    internal class LegacyInfo
    {
        public readonly string Path;
        public readonly string GUID;

        public LegacyInfo(string path, string guid)
        {
            Path = path;
            GUID = guid;
        }

        protected bool Equals(LegacyInfo other)
        {
            return Path == other.Path;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != this.GetType()) return false;
            return Equals((LegacyInfo)obj);
        }

        public override int GetHashCode()
        {
            return (Path != null ? Path.GetHashCode() : 0);
        }
    }

    internal class CreatorConfigJson
    {
        [JsonProperty("dependencies")]
        // ReSharper disable once NotAccessedField.Global
        public Dictionary<string, string> Dependencies;

        [JsonProperty("legacyAssets", NullValueHandling = NullValueHandling.Ignore)]
        // ReSharper disable once NotAccessedField.Global
        [CanBeNull] public Dictionary<string, string> LegacyAssets;

        public CreatorConfigJson(
            Dictionary<string, string> dependencies,
            [CanBeNull] Dictionary<string, string> legacyAssets
        )
        {
            Dependencies = dependencies;
            LegacyAssets = legacyAssets;
        }
    }
}
