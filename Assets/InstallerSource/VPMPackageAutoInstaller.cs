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
using System.Net.Http;
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
                installSuccessfull = await DoInstall();
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

        public static async Task<bool> DoInstall()
        {
            var configJson = AssetDatabase.GUIDToAssetPath(ConfigGuid);
            if (string.IsNullOrEmpty(configJson))
            {
                EditorUtility.DisplayDialog("ERROR", "config.json not found. installing package failed.", "ok");
                return false;
            }

            var config = new VpaiConfig(new JsonParser(File.ReadAllText(configJson, Encoding.UTF8)).Parse(JsonType.Obj));

            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent",
                "VpmPackageAutoInstaller/0.3 (github:anatawa12/VpmPackageAutoInstaller) " +
                "vrc-get/0.1.10 (github:anatawa12/vrc-get, VPAI is based on vrc-get but reimplemented in C#)");
            var env = await VrcGet.Environment.Create(client);
            var unityProject = await VrcGet.UnityProject.FindUnityProject(Directory.GetCurrentDirectory());

            await Task.WhenAll(config.vpmRepositories.Select(repoUrl => env.AddPendingRepository(repoUrl, null)));

            var includePrerelease = config.includePrerelease;

            var requestedPackages = await Task.WhenAll(config.VpmDependencies.Select(async kvp =>
            {
                var package = await env.FindPackageByName(kvp.Key, v => kvp.Value.IsSatisfied(v, includePrerelease));
                var status = unityProject.CheckAddPackage(package);
                return (package, status);
            }));

            List<VrcGet.PackageJson> toInstall;
            {
                var installRequested = requestedPackages.Where(x => x.status == VrcGet.AddPackageStatus.InstallToLocked)
                    .Select(x => x.package).ToArray();
                toInstall = await unityProject.CollectAddingPackages(env, installRequested);
                toInstall.AddRange(installRequested);
            }

            try
            {
                unityProject.CheckAddingPackages(toInstall);
            }
            catch (VrcGet.VrcGetException e)
            {
                if (!IsNoPrompt())
                    EditorUtility.DisplayDialog("ERROR!", 
                        "Installing package failed due to conflicts\n" +
                        "Please see console for more details", "OK");
                Debug.LogException(e);
                return false;
            }

            if (requestedPackages.Length == 0)
            {
                if (!IsNoPrompt())
                    EditorUtility.DisplayDialog("Nothing TO DO!", "All Packages are Installed!", "OK");
                return false;
            }

            var removeFolders = new List<string>();
            var removeFiles = new List<string>();

            void CollectLegacyAssets(List<string> removePaths, Dictionary<string, string> mapping,
                Func<string, bool> filter)
            {
                foreach (var (legacyFolder, guid) in mapping)
                {
                    // legacyAssets may use '\\' for path separator but in unity '/' is for both windows and posix
                    var legacyAssetPath = legacyFolder.Replace('\\', '/');
                    var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(legacyAssetPath);
                    if (asset != null && filter(legacyAssetPath))
                    {
                        removePaths.Add(AssetDatabase.GetAssetPath(asset));
                    }
                    else
                    {
                        var path = AssetDatabase.GUIDToAssetPath(guid);
                        if (!string.IsNullOrEmpty(path) && filter(legacyFolder))
                            removePaths.Add(path);
                    }
                }
            }

            foreach (var packageJson in toInstall)
            {
                CollectLegacyAssets(removeFolders, packageJson.LegacyFolders, Directory.Exists);
                CollectLegacyAssets(removeFiles, packageJson.LegacyFiles, File.Exists);
            }

            if (!IsNoPrompt())
            {
                var confirmMessage = new StringBuilder("You're installing the following packages:");

                foreach (var (name, version) in requestedPackages.Select(x => (x.package.Name, x.package.Version))
                             .Concat(toInstall.Select(x => (x.Name, x.Version)))
                             .Distinct())
                    confirmMessage.Append('\n').Append(name).Append(" version ").Append(version);

                if (env.PendingRepositories.Count != 0)
                {
                    confirmMessage.Append("\n\nThis will add following repositories:");
                    foreach (var localCachedRepository in env.PendingRepositories)
                        // ReSharper disable once PossibleNullReferenceException
                        confirmMessage.Append('\n').Append(localCachedRepository.CreationInfo.URL);
                }

                if (removeFiles.Count != 0 || removeFolders.Count != 0)
                {
                    confirmMessage.Append("\n\nYou're also deleting the following files/folders:");
                    foreach (var path in removeFiles.Concat(removeFolders))
                        confirmMessage.Append('\n').Append(path);
                }

                if (!EditorUtility.DisplayDialog("Confirm", confirmMessage.ToString(), "Install", "Cancel"))
                    return false;
            }

            // user confirm got. now, edit settings

            await env.SavePendingRepositories();

            foreach (var (package, status) in requestedPackages)
                if (status != VrcGet.AddPackageStatus.AlreadyAdded)
                    unityProject._manifest.AddDependency(package.Name, new VrcGet.VpmDependency(package.Version));

            await unityProject.DoAddPackagesToLocked(env, toInstall);
            await unityProject.Save();
            await env.Save();

            void RemoveLegacyAsset(string path, Action<string> remover)
            {
                try
                {
                    remover(path);
                }
                catch (IOException e)
                {
                    Debug.LogError($"error during deleting legacy: {path}: {e}");
                }
            }
            
            foreach (var path in removeFiles)
                RemoveLegacyAsset(path, File.Delete);
            foreach (var path in removeFolders)
                RemoveLegacyAsset(path, Directory.Delete);

            ResolveUnityPackageManger();
            return true;
        }

        internal static void ResolveUnityPackageManger()
        {
            System.Reflection.MethodInfo method = typeof(UnityEditor.PackageManager.Client).GetMethod("Resolve",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly);
            if (method != null)
                method.Invoke(null, null);
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

    static class Extensions
    {
        public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> self, out TKey key,
            out TValue value) => (key, value) = (self.Key, self.Value);
    }

    sealed class VpaiConfig
    {
        public readonly string[] vpmRepositories;
        public readonly bool includePrerelease;
        public readonly Dictionary<string, VrcGet.VersionRange> VpmDependencies;

        public VpaiConfig(JsonObj json)
        {
            vpmRepositories = json.Get("vpmRepositories", JsonType.List, true)?.Cast<string>()?.ToArray() ??
                              Array.Empty<string>();
            includePrerelease = json.Get("includePrerelease", JsonType.Bool, true);
            VpmDependencies = json.Get("vpmDependencies", JsonType.Obj, true)
                                  ?.ToDictionary(x => x.Item1, x => VrcGet.VersionRange.Parse((string)x.Item2))
                              ?? new Dictionary<string, VrcGet.VersionRange>();
        }
    }
}
