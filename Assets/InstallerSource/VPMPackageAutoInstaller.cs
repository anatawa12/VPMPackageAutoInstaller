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
using JetBrains.Annotations;
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

            bool installSuccessfull = DoInstall();

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

        private enum Progress
        {
            LoadingConfigJson,
            LoadingVpmManifestJson,
            LoadingGlobalSettingsJson,
            DownloadingRepositories,
            DownloadingNewRepositories,
            ResolvingDependencies,
            Prompting,
            SavingRemoteRepositories,
            DownloadingAndExtractingPackages,
            SavingConfigChanges,
            RefreshingUnityPackageManger,
            Finish
        }

        private static void ShowProgress(string message, Progress progress)
        {
            var ratio = (float)progress / (float)Progress.Finish;
            EditorUtility.DisplayProgressBar("VPAI Installer", message, ratio);
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
            EditorUtility.DisplayProgressBar("VPAI Installer", "Starting Installer...", 0.0f);
            try
            {
                return DoInstallImpl().Execute();
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                EditorUtility.DisplayDialog("ERROR", "Error installing packages", "ok");
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static async SyncedTask<bool> DoInstallImpl()
        {
            ShowProgress("Loading VPAI config...", Progress.LoadingConfigJson);
            var configJson = AssetDatabase.GUIDToAssetPath(ConfigGuid);
            if (string.IsNullOrEmpty(configJson))
            {
                EditorUtility.DisplayDialog("ERROR", "config.json not found. installing package failed.", "ok");
                return false;
            }

            var config =
                new VpaiConfig(new JsonParser(File.ReadAllText(configJson, Encoding.UTF8)).Parse(JsonType.Obj));

            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent",
                "VpmPackageAutoInstaller/0.3 (github:anatawa12/VpmPackageAutoInstaller) " +
                "vrc-get/0.1.10 (github:anatawa12/vrc-get, VPAI is based on vrc-get but reimplemented in C#)");
            ShowProgress("Loading Packages/vpm-manifest.json...", Progress.LoadingVpmManifestJson);
            var env = await VrcGet.Environment.load_default(client);
            ShowProgress("Loading settings.json...", Progress.LoadingGlobalSettingsJson);
            var unityProject = await VrcGet.UnityProject.find_unity_project(Directory.GetCurrentDirectory());
            ShowProgress("Downloading package information...", Progress.DownloadingRepositories);
            await env.load_package_infos(true);

            ShowProgress("Downloading new repositories...", Progress.DownloadingNewRepositories);
            await Task.WhenAll(config.vpmRepositories.Select(repoUrl => env.AddPendingRepository(repoUrl.url, null, repoUrl.headers)));

            ShowProgress("Resolving dependencies...", Progress.ResolvingDependencies);
            var includePrerelease = config.includePrerelease;

            var dependencies = config.VpmDependencies.Select(kvp =>
            {
                var package = env.find_package_by_name(kvp.Key, v => kvp.Value.matches(v, includePrerelease))
                    ?? throw new Exception($"package not found: {kvp.Key} version {kvp.Value}");
                return package;
            }).ToList();

            VrcGet.AddPackageRequest request;
            try
            {
                request = await unityProject.add_package_request(env, dependencies, true, includePrerelease);
            }
            catch (VrcGet.VrcGetException e)
            {
                if (!IsNoPrompt())
                    EditorUtility.DisplayDialog("ERROR!",
                        "Installing package failed due to conflicts:\n" +
                        e.Message + "\n\n" +
                        "Please see console for more details", "OK");
                Debug.LogException(e);
                return false;
            }

            if (request.locked().Count == 0)
            {
                if (!IsNoPrompt())
                    EditorUtility.DisplayDialog("Nothing TO DO!", "All Packages are Installed!", "OK");
                return false;
            }

            ShowProgress("Prompting to user...", Progress.Prompting);
            if (!IsNoPrompt())
            {
                var confirmMessage = new StringBuilder("You're installing the following packages:");

                foreach (var (name, version) in 
                         request.locked().Select(x => (Name: x.name(), Version: x.version()))
                             .Concat(request.dependencies().Select(x => (Name: x.name, Version: x.dep.version.as_single_version())))
                             .Distinct())
                    confirmMessage.Append('\n').Append(name).Append(" version ").Append(version);

                if (env.PendingRepositories.Count != 0)
                {
                    confirmMessage.Append("\n\nThis will add following repositories:");
                    foreach (var (_, url) in env.PendingRepositories)
                        // ReSharper disable once PossibleNullReferenceException
                        confirmMessage.Append('\n').Append(url);
                }

                if (request.legacy_folders().Count != 0 || request.legacy_files().Count != 0)
                {
                    confirmMessage.Append("\n\nYou're also deleting the following files/folders:");
                    foreach (var path in request.legacy_folders().Concat(request.legacy_files()))
                        confirmMessage.Append('\n').Append(path);
                }

                if (request.legacy_packages().Count != 0)
                {
                    confirmMessage.Append("\n\nYou're also deleting the following legacy Packages:");
                    foreach (var name in request.legacy_packages())
                        confirmMessage.Append("\n- ").Append(name);
                }

                if (!EditorUtility.DisplayDialog("Confirm", confirmMessage.ToString(), "Install", "Cancel"))
                    return false;
            }

            // user confirm got. now, edit settings

            ShowProgress("Saving remote repositories...", Progress.SavingRemoteRepositories);
            await env.SavePendingRepositories();

            ShowProgress("Downloading & Extracting packages...", Progress.DownloadingAndExtractingPackages);

            await unityProject.do_add_package_request(env, request);

            ShowProgress("Saving config changes...", Progress.SavingConfigChanges);
            await unityProject.save();
            await env.save();

            ShowProgress("Refreshing Unity Package Manager...", Progress.RefreshingUnityPackageManger);
            ResolveUnityPackageManger();

            ShowProgress("Almost done!", Progress.Finish);
            return true;
        }

        internal static void ResolveUnityPackageManger()
        {
            var method = typeof(UnityEditor.PackageManager.Client).GetMethod(
                name: "Resolve",
                bindingAttr: System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
                             System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
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
        [NotNull] public readonly VpaiRepository[] vpmRepositories;
        public readonly bool includePrerelease;
        [NotNull] public readonly Dictionary<string, VrcGet.VersionRange> VpmDependencies;

        public VpaiConfig(JsonObj json)
        {
            vpmRepositories = json.Get("vpmRepositories", JsonType.List, true)?.Select(v => new VpaiRepository(v))?.ToArray() ?? Array.Empty<VpaiRepository>();
            includePrerelease = json.Get("includePrerelease", JsonType.Bool, true);
            VpmDependencies = json.Get("vpmDependencies", JsonType.Obj, true)
                                  ?.ToDictionary(x => x.Item1, x => VrcGet.VersionRange.Parse((string)x.Item2))
                              ?? new Dictionary<string, VrcGet.VersionRange>();
        }
    }

    sealed class VpaiRepository
    {
        public readonly string url;
        public readonly Dictionary<string, string> headers;

        public VpaiRepository(object obj)
        {
            if (obj is string s)
            {
                url = s;
                headers = new Dictionary<string, string>();
            }
            else if (obj is JsonObj json)
            {
                url = json.Get("url", JsonType.String);
                headers = json.Get("vpmDependencies", JsonType.Obj, true)
                              ?.ToDictionary(x => x.Item1, x => (string)x.Item2)
                          ?? new Dictionary<string, string>();
            }
        }
    }
}
