using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Codice.Utils;
using JetBrains.Annotations;
using Unity.Plastic.Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Anatawa12.AutoPackageInstaller.Creator
{
    public class AutoPackageInstallerCreator : EditorWindow
    {
        [MenuItem("anatawa12/AutoPackageInstallerCreator")]
        public static void OpenGui()
        {
            GetWindow<AutoPackageInstallerCreator>();
        }

        private TextAsset _packageJsonAsset;
        private PackageJson _rootPackageJson;
        private ManifestJson _manifestJson;
        private List<PackageInfo> _packages;
        private string _gitRemoteURL;
        private string _tagName;
        private (string remote, string tag) _inferredGitInfo;

        private void OnGUI()
        {
            EditorGUILayout.LabelField("AutoPackageInstaller Creator");

            var old = _packageJsonAsset;
            _packageJsonAsset =
                (TextAsset)EditorGUILayout.ObjectField("package.json", _packageJsonAsset, typeof(TextAsset), false);
            if (_packageJsonAsset != old)
                LoadPackageInfos();

            if (_packages != null)
            {
                var url = EditorGUILayout.TextField("git url", _gitRemoteURL ?? _inferredGitInfo.remote ?? "");
                if (url != (_gitRemoteURL ?? _inferredGitInfo.remote ?? ""))
                    _gitRemoteURL = url;
                
                var tag = EditorGUILayout.TextField("git tag", _tagName ?? _inferredGitInfo.tag ?? "");
                if (tag != (_tagName ?? _inferredGitInfo.tag ?? ""))
                    _tagName = tag;

                EditorGUILayout.LabelField("The following packages will also be installed");
                foreach (var package in _packages)
                {
                    EditorGUILayout.LabelField($"{package.Id}: {package.GitURL}");
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

        private void OnProjectChange()
        {
            _manifestJson = null;
            _packageJsonAsset = null;
        }

        private void LoadPackageInfos()
        {
            if (_packageJsonAsset == null)
            {
                _packages = null;
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
                        newInferred.remote += "?path=" + HttpUtility.UrlEncode(
                            inRepoPath.Replace('\\', '/')).Replace("%2f", "/");
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
                return;
            }

            var versions = new Dictionary<string, string>();
            var order = new HashSet<string>();
            var packageJsons = new Queue<PackageJson>();

            // will be asked
            packageJsons.Enqueue(_rootPackageJson);
            versions[_rootPackageJson.Name] = "DUMMY";

            while (packageJsons.Count != 0)
            {
                var gitDependencies = packageJsons.Dequeue().GitDependencies;
                if (gitDependencies == null) continue;

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

            _packages = order.Select(id => new PackageInfo(id, versions[id])).ToList();
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
    }

#pragma warning disable CS0649
    // ReSharper disable once ClassNeverInstantiated.Global
    internal class PackageJson
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("version")] public string Version;
        [JsonProperty("dependencies")] public Dictionary<string, string> Dependencies;
        [JsonProperty("gitDependencies")] public Dictionary<string, string> GitDependencies;
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
}
