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
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using UnityEditor;
using Debug = UnityEngine.Debug;

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
            // the dylib for macos
            "e22ab70e285a450ab4bca5c1bddc0bae",
            // the dll for windows
            "7baaaa4fbe0e41bd8296df93266fb25b",
            // the so for linux
            "a3e589d0365a4398bc1e1c69f6fab14a",
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
                return DoInstallImpl();
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

        private static bool DoInstallImpl()
        {
            ShowProgress("Loading VPAI config...", Progress.LoadingConfigJson);
            var configJson = AssetDatabase.GUIDToAssetPath(ConfigGuid);
            if (string.IsNullOrEmpty(configJson))
            {
                EditorUtility.DisplayDialog("ERROR", "config.json not found. installing package failed.", "ok");
                return false;
            }

            if (!NativeUtils.Call(File.ReadAllBytes(configJson)))
                return false;

            ShowProgress("Refreshing Unity Package Manager...", Progress.RefreshingUnityPackageManger);
            ResolveUnityPackageManger();

            ShowProgress("Almost done!", Progress.Finish);
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
}
