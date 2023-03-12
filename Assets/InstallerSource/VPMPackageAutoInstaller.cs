/*
 * This is a part of https://github.com/anatawa12/VPMPackageAutoInstaller.
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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Anatawa12.SimpleJson;
using UnityEditor;
using UnityEngine;

[assembly: InternalsVisibleTo("com.anatawa12.vpm-package-auto-installer.tester")]

namespace Anatawa12.VpmPackageAutoInstaller
{
    [InitializeOnLoad]
    public static class VpmPackageAutoInstaller
    {
        private const string ConfigGuid = "9028b92d14f444e2b8c389be130d573f";

        private static readonly string[] ToBeRemoved =
        {
            ConfigGuid,
            // the dll file
            "93e23fe9bbc86463a9790ebfd1fef5eb",
            // the folder
            "4b344df74d4849e3b2c978b959abd31b",
        };

        static VpmPackageAutoInstaller()
        {
#if UNITY_5_3_OR_NEWER
            Debug.Log("Unity Compilation Env. Skipping. You should see actual run from compiled dll");
#else
            DoAutoInstall();
#endif
        }

        private static async void DoAutoInstall()
        {
            if (IsDevEnv())
            {
                Debug.Log("In dev env. skipping auto install & remove self");
                return;
            }

            await Task.Delay(1);

            bool installSuccessfull = false;
            try
            {
                installSuccessfull = DoInstall();
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                EditorUtility.DisplayDialog("ERROR", "Error installing packages", "ok");
            }

            RemoveSelf();

            if (!installSuccessfull)
                AssetDatabase.Refresh();
        }

        internal static bool IsDevEnv()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup buildTargetGroup = BuildPipeline.GetBuildTargetGroup(target);
            return PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup)
                .Contains("VPM_PACKAGE_AUTO_INSTALLER_DEV_ENV");
        }

        private static bool IsNoPrompt()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup buildTargetGroup = BuildPipeline.GetBuildTargetGroup(target);
            return PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup)
                .Contains("VPM_PACKAGE_AUTO_INSTALLER_NO_PROMPT");
        }

        public static bool DoInstall()
        {
            var configJson = AssetDatabase.GUIDToAssetPath(ConfigGuid);
            if (string.IsNullOrEmpty(configJson))
            {
                EditorUtility.DisplayDialog("ERROR", "config.json not found. installing package failed.", "ok");
                return false;
            }

            var config = new JsonParser(File.ReadAllText(configJson, Encoding.UTF8)).Parse(JsonType.Obj);
            var vpmManifest = VpmManifest.Load();
            var vpmGlobalSetting = VpmGlobalSetting.Load();

            var vpmRepositories = config.Get("vpmRepositories", JsonType.List, true) ?? new List<object>();
            var allVpmRepos = (
                    from urlInObj in vpmRepositories
                    let repoURL = urlInObj as string
                    where repoURL != null
                    select new VpmUserRepository(repoURL))
                .ToList();
            var vpmRepos = allVpmRepos.Where(vpmRepo => !vpmGlobalSetting.RepositoryExists(vpmRepo.Url)).ToList();

            var dependencies = config.Get("vpmDependencies", JsonType.Obj, true) ?? new JsonObj();
            var updates = (
                    from package in dependencies.Keys
                    let requestedVersion = dependencies.Get(package, JsonType.String)
                    let version = ResolveVersion(package, requestedVersion, allVpmRepos)
                    where vpmManifest.Dependencies.NeedsUpdate(package, version) ||
                          vpmManifest.Locked.NeedsUpdate(package, version)
                    select (package, version))
                .ToList();

            if (updates.Count == 0)
            {
                if (!IsNoPrompt())
                    EditorUtility.DisplayDialog("Nothing TO DO!", "All Packages are Installed!", "OK");
                return false;
            }

            var removePaths = new List<string>();
            var legacyAssets = config.Get("legacyAssets", JsonType.Obj, true);
            if (legacyAssets != null)
            {
                foreach (var key in legacyAssets.Keys)
                {
                    // legacyAssets may use '\\' for path separator but in unity '/' is for both windows and posix
                    var legacyAssetPath = key.Replace('\\', '/');
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(legacyAssetPath);
                    if (asset != null)
                    {
                        removePaths.Add(AssetDatabase.GetAssetPath(asset));
                    }
                    else
                    {
                        var guid = legacyAssets.Get(key, JsonType.String);
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        if (!string.IsNullOrEmpty(path))
                            removePaths.Add(path);
                    }
                }
            }

            var confirmMessage = "You're installing the following packages:\n";
            confirmMessage += string.Join("\n", updates.Select(p => $"{p.package} version {p.version}"));

            if (removePaths.Count != 0)
            {
                confirmMessage += "\n\nYou're also deleting the following files/folders";
                confirmMessage += string.Join("\n", removePaths);
            }

            if (!IsNoPrompt() && !EditorUtility.DisplayDialog("Confirm", confirmMessage, "Install", "Cancel"))
                return false;

            foreach (var repo in vpmRepos)
                vpmGlobalSetting.AddPackageRepository(repo);

            foreach (var (key, value) in updates)
                vpmManifest.AddPackage(key, value);

            vpmGlobalSetting.Save();
            vpmManifest.Save();

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

            VRChatPackageManager.CallResolver();
            return true;
        }

        private static string ResolveVersion(string package, string requestedVersion, List<VpmUserRepository> vpmRepos)
        {
            if (!requestedVersion.StartsWith("~") && !requestedVersion.StartsWith("^")) return requestedVersion;

            var caret = requestedVersion[0] == '^';
            var requestedParsed = Version.Parse(requestedVersion.Substring(1));
            var upperBound = caret ? UpperBoundForCaret(requestedParsed) : UpperBoundForTilda(requestedParsed);
            var allowPrerelease = requestedParsed.Prerelease != null;
            return vpmRepos.SelectMany(repo => repo.GetVersions(package).Select(Version.Parse))
                .Where(v => (allowPrerelease || v.Prerelease == null) && requestedParsed <= v && v < upperBound)
                .Concat(new[] { requestedParsed })
                .Max()
                .ToString();
        }

        private static Version UpperBoundForTilda(Version requestedVersion) =>
            requestedVersion.HasMinor
                ? new Version(requestedVersion.Major, requestedVersion.Minor + 1, -1)
                : new Version(requestedVersion.Major + 1, -1, -1);

        private static Version UpperBoundForCaret(Version requestedVersion)
        {
            if (requestedVersion.Major != 0)
                return new Version(requestedVersion.Major + 1, -1, -1);
            if (requestedVersion.Minor != 0)
                return new Version(requestedVersion.Major, requestedVersion.Minor + 1, -1);
            if (requestedVersion.Patch != 0)
                return new Version(requestedVersion.Major, requestedVersion.Minor, requestedVersion.Patch + 1);
            // this mean, 0 or 0.0 or 0.0.0. use 1 or 0.1 or 0.0.1
            if (requestedVersion.HasPatch)
                return new Version(0, 0, 1);
            if (requestedVersion.HasMinor)
                return new Version(0, 1, -1);
            return new Version(1, -1, -1);
        }

        public static void RemoveSelf()
        {
            foreach (var remove in ToBeRemoved)
            {
                RemoveFileAsset(remove);
            }

            BurstPatch.UpdateBurstAssemblyFolders();
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
}
