/*
 * This is a part of https://github.com/anatawa12/AutoPackageInstaller.
 * 
 * MIT License
 * 
 * Copyright (c) 2022 anatawa12
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using JetBrains.Annotations;
using UnityEditor;
using UnityEngine;

[assembly: InternalsVisibleTo("com.anatawa12.auto-package-installer.tester")]

namespace Anatawa12.AutoPackageInstaller
{
    [InitializeOnLoad]
    public class AutoPackageInstaller
    {
        private const string TesterGuid = "40f4470be29a43a792b7c41576813ea3";
        private const string ConfigGuid = "9028b92d14f444e2b8c389be130d573f";
        private const string ManifestPath = "Packages/manifest.json";

        private static readonly string[] ToBeRemoved =
        {
            ConfigGuid,
            // the C# file
            "30732659753784f469c8c521aa469152",
            // the asmdef file
            "f7306773db58a40f2b8c5b6ed99db57b",
            // the folder
            "4b344df74d4849e3b2c978b959abd31b",
        };

        static AutoPackageInstaller()
        {
            var testerExist = !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(TesterGuid));
            if (testerExist)
            {
                Debug.Log("Tester found. skipping auto install & remove self");
                return;
            }

            DoInstall();
            RemoveSelf();
        }

        public AutoPackageInstaller()
        {
        }

        public static void DoInstall()
        {
            var configJson = AssetDatabase.GUIDToAssetPath(ConfigGuid);
            if (string.IsNullOrEmpty(configJson))
            {
                EditorUtility.DisplayDialog("ERROR", "config.json not found. installing package failed.", "ok");
                return;
            }

            var config = new JsonParser(File.ReadAllText(configJson, Encoding.UTF8)).Parse(JsonType.Obj);
            var manifest = new JsonParser(File.ReadAllText(ManifestPath, Encoding.UTF8)).Parse(JsonType.Obj);

            var dependencies = config.Get("dependencies", JsonType.Obj);
            var manifestDependencies = manifest.GetOrPut("dependencies", () => new JsonObj(), JsonType.Obj);
            var updates = (
                    from key in dependencies.Keys
                    let value = dependencies.Get(key, JsonType.String)
                    let version = manifestDependencies.Get(key, JsonType.String, true)
                    where version == null || ShouldUpdatePackage(version, value)
                    select (key, value))
                .ToList();


            var removePaths = new List<string>();
            var legacyFolders = config.Get("legacyFolders", JsonType.Obj);
            foreach (var key in legacyFolders.Keys)
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(key);
                if (asset != null)
                {
                    removePaths.Add(AssetDatabase.GetAssetPath(asset));
                }
                else
                {
                    var guid = legacyFolders.Get(key, JsonType.String);
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path))
                        removePaths.Add(path);
                }
            }

            /*
            // TODO: Research about legacyFiles and uncomment this
            var legacyFiles = config.Get("legacyFiles", JsonType.Obj);
            foreach (var key in legacyFiles.Keys)
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(key);
                if (asset != null)
                {
                    removePaths.Add(AssetDatabase.GetAssetPath(asset));
                }
                else
                {
                    var guid = legacyFiles.Get(key, JsonType.String);
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path))
                        removePaths.Add(path);
                }
            }
            */

            var confirmMessage = "You're installing the following packages:\n";
            confirmMessage += string.Join("\n", updates.Select(p => $"{p.key} version {GetVersionName(p.value)}"));

            if (removePaths.Count != 0)
            {
                confirmMessage += "\n\nYou're also deleting the following files/folders";
                confirmMessage += string.Join("\n", removePaths);
            }

            if (!EditorUtility.DisplayDialog("Confirm", confirmMessage, "Install", "Cancel"))
                return;

            foreach (var (key, value) in updates)
                manifestDependencies.Put(key, value, JsonType.String);

            try
            {
                File.Copy(ManifestPath, ManifestPath + ".bak", true);
            }
            catch (IOException e)
            {
                Debug.LogError($"error during creating backup: {e}");
            }

            File.WriteAllText(ManifestPath, JsonWriter.Write(manifest));
            SetDirty(ManifestPath);

            try
            {
                foreach (var removePath in removePaths)
                    if (File.Exists(removePath))
                        File.Delete(removePath);
                    else
                        Directory.Delete(removePath, true);
            }
            catch (IOException e)
            {
                Debug.LogError($"error during deleting legacy: {e}");
            }
        }

        private static string GetVersionName(string versionOrGitUrl)
        {
            if (versionOrGitUrl.StartsWith("git") || versionOrGitUrl.Contains(".git"))
            {
                var index = versionOrGitUrl.IndexOf('#');
                if (index == -1)
                    return "latest";
                var name = versionOrGitUrl.Substring(index + 1);
                if (name.StartsWith("v"))
                    name = name.Substring(1);
                return name;
            }

            return versionOrGitUrl;
        }

        private static bool ShouldUpdatePackage(string current, string value)
        {
            if (Version.TryParse(current, out var currentVersion) && Version.TryParse(value, out var valueVersion))
                return currentVersion.CompareTo(valueVersion) < 0;
            return true;
        }

        public static void RemoveSelf()
        {
            foreach (var remove in ToBeRemoved)
            {
                RemoveFileAsset(remove);
            }

            AssetDatabase.Refresh();
        }

        private static void RemoveFileAsset(string guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (File.Exists(path))
            {
                SetDirty(path);
                try
                {
                    File.Delete(path);
                    File.Delete(path + ".meta");
                }
                catch (IOException e)
                {
                    Debug.LogError($"error removing installer: {e}");
                }
            }
            else if (Directory.Exists(path))
            {
                SetDirty(path);
                try
                {
                    Directory.Delete(path);
                    File.Delete(path + ".meta");
                }
                catch (IOException e)
                {
                    Debug.LogError($"error removing installer: {e}");
                }
            }
        }

        private static void SetDirty(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset != null) EditorUtility.SetDirty(asset);
        }
    }

    // Mini VPM Simulator

    #region VPM

    internal class VRChatPackageManager
    {
        public static string GlobalFoler = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRChatCreatorCompanion");

        public static string GlobalSettingFile = Path.Combine(GlobalFoler, "settings.json");
        public static string GlobalReposFolder = Path.Combine(GlobalFoler, "Repos");
        public static string ProjectFolder = Directory.GetCurrentDirectory();
        public static string VpmManifest = Path.Combine(ProjectFolder, "Packages", "vpm-manifest.json");

        public enum AddPackageRepositoryResult
        {
            Success,
            AlreadyExists
        }

        public static AddPackageRepositoryResult AddPackageRepository(string url)
        {
            // read setting
            var setting = new JsonParser(File.ReadAllText(GlobalSettingFile, Encoding.UTF8)).Parse(JsonType.Obj);
            var userRepos = setting.Get("userRepos", JsonType.List);

            // find existing
            if (userRepos.Any(o => o is JsonObj userRepo && userRepo.Get("url", JsonType.String) == url))
                return AddPackageRepositoryResult.AlreadyExists;

            // get repo contents
            var response = new HttpClient().GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new IOException($"Getting {url} failed");

            var repoJsonString = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var repoJson = new JsonParser(repoJsonString).Parse(JsonType.Obj);

            // generate local repo path
            var localRepoPath = Path.Combine(GlobalReposFolder, Guid.NewGuid() + ".json");
            while (File.Exists(localRepoPath))
                localRepoPath = Path.Combine(GlobalReposFolder, Guid.NewGuid() + ".json");

            var repoName = repoJson.Get("name", JsonType.String);

            // create local repo info
            var localRepo = new JsonObj();
            localRepo.Put("repo", repoJson, JsonType.Obj);

            var creationInfo = new JsonObj();
            creationInfo.Put("localPath", localRepoPath, JsonType.String);
            creationInfo.Put("url", url, JsonType.String);
            creationInfo.Put("name", repoName, JsonType.String);
            localRepo.Put("CreationInfo", creationInfo, JsonType.Obj);

            var description = new JsonObj();
            description.Put("name", repoName, JsonType.String);
            description.Put("type", "JsonRepo", JsonType.String);
            localRepo.Put("Description", description, JsonType.Obj);

            File.WriteAllText(localRepoPath, JsonWriter.Write(localRepo), Encoding.UTF8);

            // update settings
            var newRepo = new JsonObj();
            newRepo.Put("localPath", localRepoPath, JsonType.String);
            newRepo.Put("url", url, JsonType.String);
            newRepo.Put("name", repoName, JsonType.String);
            userRepos.Add(newRepo);

            File.WriteAllText(GlobalSettingFile, JsonWriter.Write(setting), Encoding.UTF8);

            return AddPackageRepositoryResult.Success;
        }

        public enum AddPackageResult
        {
            Updated,
            AlreadyExists
        }

        // add to dependencies & lock and run resolver to add package
        public static AddPackageResult AddPackage(string package, string version, bool overrideNewer = false,
            bool callResolver = true)
        {
            var requestedIsSemver = Version.TryParse(version, out var requestedParsed);
            JsonObj manifest;
            // read setting
            try
            {
                manifest = new JsonParser(File.ReadAllText(VpmManifest, Encoding.UTF8)).Parse(JsonType.Obj);
            }
            catch (FileNotFoundException)
            {
                manifest = new JsonObj();
            }

            var dependencies = manifest.GetOrPut("dependencies", () => new JsonObj(), JsonType.Obj);
            var locked = manifest.GetOrPut("locked", () => new JsonObj(), JsonType.Obj);

            var foundInDependencies = dependencies.GetOrPut(package, () => new JsonObj(), JsonType.Obj);
            var foundVersion = foundInDependencies.Get("version", JsonType.String, true);
            // if version in dependencies is newer, do not update
            if (!overrideNewer && requestedIsSemver && foundVersion != null
                && Version.TryParse(foundVersion, out var foundParsed) && foundParsed >= requestedParsed)
                return AddPackageResult.AlreadyExists;

            foundInDependencies.Put("version", version, JsonType.String);


            // add or update lock
            var lockedComponent = locked.GetOrPut(package, () => new JsonObj(), JsonType.Obj);
            var lockedVersion = lockedComponent.Get("version", JsonType.String, true);

            // if version in lock is newer, do not update
            if (!overrideNewer && requestedIsSemver && lockedVersion != null
                && Version.TryParse(lockedVersion, out var lockedParsed) && lockedParsed >= requestedParsed)
                return AddPackageResult.AlreadyExists;

            lockedComponent.Put("version", version, JsonType.String);

            // save manifest
            File.WriteAllText(VpmManifest, JsonWriter.Write(manifest), Encoding.UTF8);

            if (callResolver)
                CallResolver();
            return AddPackageResult.Updated;
        }

        public static void CallResolver()
        {
            try
            {
                // first, call VPMProjectManifest.Resolve
                {
                    var asm = Assembly.Load("vpm-core-lib");
                    var type = asm.GetType("VRC.PackageManagement.Core.Types.Packages.VPMProjectManifest");
                    var method = type.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(string) }, null);
                    method.Invoke(null, new object[] { ProjectFolder });
                }
                // first, call UnityEditor.PackageManager.Client.Resolve
                {
                    var method = typeof(UnityEditor.PackageManager.Client)
                        .GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (method != null)
                        method.Invoke(null, null);
                    AssetDatabase.Refresh();
                }
                return;
            }
            catch (Exception e)
            {
                Debug.LogError("Invoking VPMProjectManifest.Resolve failed. ");
                Debug.LogError(e);
            }

            SessionState.SetBool("PROJECT_LOADED", false);
        }
    }

    internal readonly struct Version : IComparable<Version>
    {
        public int Major { get; }
        private readonly int _minor;
        public int Minor => _minor == -1 ? 0 : _minor;
        public bool HasMinor => _minor != -1;
        private readonly int _patch;
        public int Patch => _patch == -1 ? 0 : _patch;
        public bool HasPatch => _patch != -1;
        public string[] Prerelease { get; }
        public string Build { get; }

        public Version(int major, int minor, int patch, params string[] prerelease)
            : this(major, minor, patch, prerelease, null)
        {
        }

        public Version(int major, int minor, int patch, string[] prerelease, string build)
        {
            if (major < 0) throw new ArgumentException("must be zero or positive", nameof(major));
            if (minor == -1 && patch != -1)
                throw new ArgumentException("minor version must be defined if patch is defined", nameof(patch));
            if (minor != -1 && minor < 0) throw new ArgumentException("must be zero or positive", nameof(major));
            if (patch != -1 && patch < 0) throw new ArgumentException("must be zero or positive", nameof(major));
            Major = major;
            _minor = minor;
            _patch = patch;
            Prerelease = prerelease?.Length == 0 ? null : prerelease;
            Build = build?.Length == 0 ? null : build;
        }

        public override string ToString()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(Major);
            if (HasMinor)
            {
                builder.Append('.').Append(Minor);
                if (HasPatch)
                {
                    builder.Append('.').Append(Patch);
                }
            }

            if (Prerelease != null)
            {
                builder.Append('-').Append(Prerelease[0]);
                for (var i = 1; i < Prerelease.Length; i++)
                    builder.Append('.').Append(Prerelease[i]);
            }

            if (Build != null)
                builder.Append('+').Append(Build);

            return builder.ToString();
        }

        public int CompareTo(Version other)
        {
            var majorComparison = Major.CompareTo(other.Major);
            if (majorComparison != 0) return majorComparison;
            var minorComparison = Minor.CompareTo(other.Minor);
            if (minorComparison != 0) return minorComparison;
            var patchComparison = Patch.CompareTo(other.Patch);
            if (patchComparison != 0) return patchComparison;
            // both are release: same version
            if (Prerelease == null && other.Prerelease == null) return 0;
            // this is release but other is prerelease: this is later
            if (Prerelease == null && other.Prerelease != null) return 1;
            // this is release but other is prerelease: other is later
            if (Prerelease != null && other.Prerelease == null) return -1;
            System.Diagnostics.Debug.Assert(Prerelease != null && other.Prerelease != null);

            // both are prerelease
            var minLen = Math.Min(Prerelease.Length, other.Prerelease.Length);
            for (var i = 0; i < minLen; i++)
            {
                var thisComponent = Prerelease[i];
                var thisIsInt = int.TryParse(thisComponent, out var thisInt);
                var otherComponent = other.Prerelease[i];
                var otherIsInt = int.TryParse(otherComponent, out var otherInt);
                if (thisIsInt && otherIsInt)
                {
                    var intComparison = thisInt.CompareTo(otherInt);
                    if (intComparison != 0) return intComparison;
                }
                else if (!thisIsInt && !otherIsInt)
                {
                    var strComparison = String.Compare(thisComponent, otherComponent, StringComparison.Ordinal);
                    if (strComparison != 0) return strComparison;
                }
                else if (thisIsInt && !otherIsInt)
                {
                    return -1;
                }
                else if (!thisIsInt && otherIsInt)
                {
                    return 1;
                }
            }

            return Prerelease.Length.CompareTo(other.Prerelease.Length);
        }

        public static bool TryParse(string str, out Version version)
        {
            version = default;
            string[] prerelease = null;
            string build = null;
            int major = -1, minor = -1, patch = -1;

            var plus = str.IndexOf('+');
            if (plus != -1)
            {
                build = str.Substring(plus + 1);
                str = str.Substring(0, plus);
            }

            var hyphen = str.IndexOf('-');
            if (hyphen != -1)
            {
                prerelease = str.Substring(hyphen + 1).Split('.');
                if (prerelease.Any(x => x.Length == 0)) return false;
                str = str.Substring(0, hyphen);
            }

            var versionCore = str.Split('.');
            if (versionCore.Length > 3) return false;
            if (1 <= versionCore.Length && !int.TryParse(versionCore[0], out major)) return false;
            if (2 <= versionCore.Length && !int.TryParse(versionCore[1], out minor)) return false;
            if (3 <= versionCore.Length && !int.TryParse(versionCore[2], out patch)) return false;
            version = new Version(major, minor, patch, prerelease, build);
            return true;
        }

        public static bool operator ==(Version a, Version b) => a.Equals(b);
        public static bool operator !=(Version a, Version b) => !a.Equals(b);
        public static bool operator <(Version a, Version b) => a.CompareTo(b) < 0;
        public static bool operator <=(Version a, Version b) => a.CompareTo(b) <= 0;
        public static bool operator >(Version a, Version b) => a.CompareTo(b) > 0;
        public static bool operator >=(Version a, Version b) => a.CompareTo(b) >= 0;

        public bool Equals(Version other)
        {
            return Major == other.Major && _minor == other._minor && _patch == other._patch &&
                   StructuralComparisons.StructuralEqualityComparer.Equals(Prerelease, other.Prerelease) 
                   && Build == other.Build;
        }

        public override bool Equals(object obj)
        {
            return obj is Version other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = Major;
                hashCode = (hashCode * 397) ^ _minor;
                hashCode = (hashCode * 397) ^ _patch;
                hashCode = (hashCode * 397) ^ (Prerelease != null
                    ? StructuralComparisons.StructuralEqualityComparer.GetHashCode(Prerelease)
                    : 0);
                hashCode = (hashCode * 397) ^ (Build != null ? Build.GetHashCode() : 0);
                return hashCode;
            }
        }
    }

    #endregion

    // minimum json parser with JsonObj, List<object>, string, long, double, bool, and null
    // This doesn't use Dictionary because it can't save order

    #region Json

    sealed class JsonObj : IEnumerable<(string, object)>
    {
        [NotNull] internal readonly List<(string, object)> Obj = new List<(string, object)>();

        public JsonObj()
        {
        }

        public int Count => Obj.Count;
        public IEnumerable<string> Keys => Obj.Select(pair => pair.Item1);

        public void Add(string key, object value)
        {
            Obj.Add((key, value));
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        IEnumerator<(string, object)> IEnumerable<(string, object)>.GetEnumerator()
        {
            return GetEnumerator();
        }

        public List<(string, object)>.Enumerator GetEnumerator()
        {
            return Obj.GetEnumerator();
        }

        public T Get<T>(string key, TypeDesc<T> typeDesc, bool optional = false)
        {
            return typeDesc.Cast(Obj.FirstOrDefault(p => p.Item1 == key).Item2, optional);
        }

        public T GetOrPut<T>(string key, Func<T> getDefault, TypeDesc<T> typeDesc)
        {
            var pair = Obj.FirstOrDefault(p => p.Item1 == key);
            if (pair.Item1 != null) return typeDesc.Cast(pair.Item2);
            T value = getDefault();
            Put(key, value, typeDesc);
            return value;
        }

        public void Put<T>(string key, T value, TypeDesc<T> typeDesc)
        {
            for (int i = 0; i < Obj.Count; i++)
            {
                if (Obj[i].Item1 == key)
                {
                    var pair = Obj[i];
                    pair.Item2 = value;
                    Obj[i] = pair;
                    return;
                }
            }

            Add(key, value);
        }

        private bool Equals(JsonObj other) => Obj.Equals(other.Obj);

        public override bool Equals(object obj) => ReferenceEquals(this, obj) || obj is JsonObj other && Equals(other);

        public override int GetHashCode() => Obj.GetHashCode();
    }

    static class JsonType
    {
#pragma warning disable CS0414
        public static readonly TypeDesc<JsonObj> Obj = default;
        public static readonly TypeDesc<List<object>> List = default;
        public static readonly TypeDesc<string> String = default;
        public static readonly TypeDesc<double> Number = default;
        public static readonly TypeDesc<bool> Bool = default;
        public static readonly TypeDesc<object> Any = default;
#pragma warning restore CS0414
    }

    // dummy struct for generic
    readonly struct TypeDesc<T>
    {
        public T Cast(object value, bool optional = false)
        {
            if (new object() is T) return (T)value;
            return value == null && optional ? default
                : value is T t ? t
                : throw new InvalidOperationException($"unexpected type: {value?.GetType()?.ToString() ?? "null"}");
        }
    }

    sealed class JsonParser
    {
        enum TokenType : sbyte
        {
            None,

            // {}
            OpenBrace,
            CloseBrace,

            // []
            OpenBracket,
            CloseBracket,
            Comma,
            Colon,
            Literal,
        }

        private readonly string _input;
        private int _cursor;

        public JsonParser(string input)
        {
            _input = input;
            _cursor = 0;
        }

        public T Parse<T>(TypeDesc<T> typeDesc)
        {
            var result = ParseValue();
            CheckEof();
            return typeDesc.Cast(result);
        }

        private object ParseValue()
        {
            var token = NextToken();
            switch (token.Item1)
            {
                case TokenType.Literal:
                    return token.Item2;
                case TokenType.OpenBracket:
                    List<object> list = new List<object>();
                    if ((token = NextToken()).Item1 != TokenType.CloseBracket)
                    {
                        _token = token;
                        list.Add(ParseValue());
                        while ((token = NextToken()).Item1 != TokenType.CloseBracket)
                        {
                            if (token.Item1 != TokenType.Comma)
                                throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");
                            list.Add(ParseValue());
                        }
                    }

                    return list;

                case TokenType.OpenBrace:
                    JsonObj dict = new JsonObj();
                    if ((token = NextToken()).Item1 != TokenType.CloseBrace)
                    {
                        if (token.Item1 != TokenType.Literal || !(token.Item2 is string key0))
                            throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");

                        if ((token = NextToken()).Item1 != TokenType.Colon)
                            throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");

                        dict.Add(key0, ParseValue());

                        while ((token = NextToken()).Item1 != TokenType.CloseBrace)
                        {
                            if (token.Item1 != TokenType.Comma)
                                throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");

                            if ((token = NextToken()).Item1 != TokenType.Literal || !(token.Item2 is string key))
                                throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");

                            if ((token = NextToken()).Item1 != TokenType.Colon)
                                throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");

                            dict.Add(key, ParseValue());
                        }
                    }

                    return dict;

                default:
                    throw new InvalidOperationException($"invalid json: unexpected token: {token.Item1}");
            }
        }

        private (TokenType, object) _token;

        private (TokenType, object) NextToken()
        {
            if (_token.Item1 != TokenType.None)
            {
                var result = _token;
                _token = default;
                return result;
            }

            return ComputeNextToken();
        }

        private (TokenType, object) ComputeNextToken()
        {
            char c;
            while ((c = GetMoveChar()) == '\u0020' || c == '\u000A' || c == '\u000D' || c == '\u0009')
            {
            }
            // now c is first non-whitespace char

            switch (c)
            {
                case '{': return (TokenType.OpenBrace, null);
                case '}': return (TokenType.CloseBrace, null);
                case '[': return (TokenType.OpenBracket, null);
                case ']': return (TokenType.CloseBracket, null);
                case ',': return (TokenType.Comma, null);
                case ':': return (TokenType.Colon, null);

                // keyword literals
                case 't':
                    if (GetMoveChar() != 'r' || GetMoveChar() != 'u' || GetMoveChar() != 'e')
                        throw new InvalidOperationException("invalid json: unknown token starting 't'");
                    return (TokenType.Literal, true);
                case 'f':
                    if (GetMoveChar() != 'a' || GetMoveChar() != 'l' || GetMoveChar() != 's' || GetMoveChar() != 'e')
                        throw new InvalidOperationException("invalid json: unknown token starting 'f'");
                    return (TokenType.Literal, false);
                case 'n':
                    if (GetMoveChar() != 'u' || GetMoveChar() != 'l' || GetMoveChar() != 'l')
                        throw new InvalidOperationException("invalid json: unknown token starting 'n'");
                    return (TokenType.Literal, null);

                // string literal
                case '"': return (TokenType.Literal, StringLiteral());

                // numeric literal
                case '-':
                case '+':
                case '0':
                case '1':
                case '2':
                case '3':
                case '4':
                case '5':
                case '6':
                case '7':
                case '8':
                case '9':
                    return (TokenType.Literal, NumericLiteral(c));
                case '\0':
                    throw new InvalidOperationException("invalid json: unexpected eof");
                default:
                    throw new InvalidOperationException(InvalidChar(c));
            }
        }

        private string StringLiteral()
        {
            StringBuilder builder = new StringBuilder();
            char c;
            while ((c = GetMoveChar()) != '"')
            {
                if (c == '\\')
                {
                    switch (c = GetMoveChar())
                    {
                        case '"':
                            builder.Append('"');
                            break;
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '/':
                            builder.Append('/');
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            c = (char)0;
                            c |= (char)(HexChar() << 24);
                            c |= (char)(HexChar() << 16);
                            c |= (char)(HexChar() << 8);
                            c |= (char)(HexChar() << 0);
                            builder.Append(c);
                            break;
                        default:
                            throw new InvalidOperationException(InvalidChar(c));
                    }
                }
                else if ('\u0020' <= c)
                {
                    builder.Append(c);
                }
                else
                {
                    throw new InvalidOperationException(InvalidChar(c));
                }
            }

            return builder.ToString();
        }

        private int HexChar()
        {
            var c = GetMoveChar();
            if ('0' <= c && c <= '9')
                return c - '0';
            if ('a' <= c && c <= 'f')
                return c - 'a' + 10;
            if ('A' <= c && c <= 'F')
                return c - 'A' + 10;
            throw new InvalidOperationException(InvalidChar(c));
        }

        private static string InvalidChar(char c)
        {
            return $"invalid json: invalid char: '{c}' ({(int)c:x2})";
        }

        private void CheckEof()
        {
            char c;
            while (char.IsWhiteSpace(c = GetMoveChar()))
            {
            }

            if (c != '\0')
                throw new InvalidOperationException(InvalidChar(c));
        }

        private object NumericLiteral(char c)
        {
            var start = _cursor - 1;
            if (c == '+' || c == '-')
                c = GetMoveChar();

            if (c == '0')
                c = GetMoveChar();
            else
                c = SkipIntegerLiteral(c);

            if (c == '.')
            {
                c = SkipIntegerLiteral(GetMoveChar());
            }

            if (c == 'e')
            {
                if ((c = GetMoveChar()) == '+' || c == '-')
                    c = GetMoveChar();
                c = SkipIntegerLiteral(c);
            }

            _cursor--;
            var str = _input.Substring(start, _cursor - start);

            //if (long.TryParse(str, out var l)) return l;
            if (double.TryParse(str, out var d)) return d;
            throw new InvalidOperationException($"invalid json: invalid number: {str}");
        }

        private char SkipIntegerLiteral(char c)
        {
            long integer = 0;
            while (true)
            {
                if ('0' <= c && c <= '9')
                {
                    if (integer >= long.MaxValue / 10)
                        throw new InvalidOperationException("invalid json: number too big");
                    integer = integer * 10 + (c - '0');
                }
                else
                {
                    return c;
                }

                c = GetMoveChar();
            }
        }

        private char GetMoveChar()
        {
            var cur = _cursor++;
            return cur >= _input.Length ? '\0' : _input[cur];
        }
    }

    static class JsonWriter
    {
        public static string Write(object obj)
        {
            StringBuilder builder = new StringBuilder();
            WriteToBuilder(obj, builder, "");
            return builder.ToString();
        }

        private static void WriteToBuilder(object o, StringBuilder builder, string indent)
        {
            if (o == null) builder.Append("null");
            else if (o is string s) WriteString(builder, s);
            //else if (o is long l) builder.Append(l);
            else if (o is double d) builder.Append(d);
            else if (o is bool b) builder.Append(b ? "true" : "false");
            else if (o is JsonObj dict) WriteObject(builder, dict, indent);
            else if (o is List<object> list) WriteArray(builder, list, indent);
            else throw new ArgumentException($"unsupported type: {o.GetType()}", nameof(o));
        }

        private static void WriteString(StringBuilder builder, string s)
        {
            builder.Append('"');
            foreach (var c in s)
            {
                if (c == '"') builder.Append("\\\"");
                else if (c == '\\') builder.Append("\\\\");
                else if (c < '\u0020') builder.Append($"'\\u{(int)c:x4}'");
                else builder.Append(c);
            }

            builder.Append('"');
        }

        private static void WriteObject(StringBuilder builder, JsonObj dict, string indent)
        {
            if (dict.Count == 0)
            {
                builder.Append("{}");
                return;
            }


            var oldIndent = indent;
            builder.Append('{').Append(Environment.NewLine);
            indent += "  ";
            using (List<(string, object)>.Enumerator e = dict.GetEnumerator())
            {
                e.MoveNext();
                while (true)
                {
                    var pair = e.Current;
                    var hasNext = e.MoveNext();
                    builder.Append(indent);
                    WriteString(builder, pair.Item1);
                    builder.Append(": ");
                    WriteToBuilder(pair.Item2, builder, indent);
                    if (hasNext)
                        builder.Append(',');
                    builder.Append(Environment.NewLine);
                    if (!hasNext) break;
                }
            }

            builder.Append(oldIndent).Append('}');
        }

        private static void WriteArray(StringBuilder builder, List<object> list, string indent)
        {
            if (list.Count == 0)
            {
                builder.Append("[]");
                return;
            }


            var oldIndent = indent;
            builder.Append('[').Append(Environment.NewLine);
            indent += "  ";
            using (List<object>.Enumerator e = list.GetEnumerator())
            {
                e.MoveNext();
                while (true)
                {
                    var value = e.Current;
                    var hasNext = e.MoveNext();
                    builder.Append(indent);
                    WriteToBuilder(value, builder, indent);
                    if (hasNext)
                        builder.Append(',');
                    builder.Append(Environment.NewLine);
                    if (!hasNext) break;
                }
            }

            builder.Append(oldIndent).Append(']');
        }
    }

    #endregion
}
