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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
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

    static unsafe class NativeUtils
    {
        private static readonly List<GCHandle> FixedKeep = new List<GCHandle>();

        public static bool Call(byte[] bytes)
        {
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

            var data = new NativeCsData
            {
                version = 1,
                version_mismatch = Marshal.GetFunctionPointerForDelegate(version_mismatch),

                config_ptr = handle.AddrOfPinnedObject(),
                config_len = bytes.Length,

                display_dialog = Marshal.GetFunctionPointerForDelegate(display_dialog),
                log_error = Marshal.GetFunctionPointerForDelegate(log_error),
                guid_to_asset_path = Marshal.GetFunctionPointerForDelegate(guid_to_asset_path),
            };

            try
            {
                // TODO find dll based on platform
                var path = AssetDatabase.GUIDToAssetPath("e22ab70e285a450ab4bca5c1bddc0bae");
                if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("lib not found");
                using (var lib = LibLoader.LibLoader.LoadLibrary(path))
                {
                    var ptr = lib.GetAddress("vpai_native_entry_point");
                    Debug.Log($"found vpai_native_entry_point: {ptr}");

                    var nativeEntryPoint = Marshal.GetDelegateForFunctionPointer<vpai_native_entry_point_t>(ptr);

                    return nativeEntryPoint(data);
                }
            }
            finally
            {

                while (FixedKeep.Count != 0)
                {
                    FixedKeep[FixedKeep.Count - 1].Free();
                    FixedKeep.RemoveAt(FixedKeep.Count - 1);
                }
            }
        }

        private static void VersionMismatch()
        {
            EditorUtility.DisplayDialog("ERROR", "VPAI Internal Error! Installer is broken or THIS IS A BUG!", "OK");
        }

        private static bool DisplayDialog(in RustStr title, in RustStr message, in RustStr ok, in RustStr cancel)
        {
            return EditorUtility.DisplayDialog(title.AsString(), message.AsString(), ok.AsString(), cancel.AsString());
        }

        private static void LogError(in RustStr message)
        {
            Debug.LogError(message.AsString());
        }

        private static void GuidToAssetPath(in RustGUID guid, out RustStr path)
        {
            var chars = new char[128/4];
            for (int i = 0; i < 128 / 8; i++)
            {
                var b = guid.bytes[i];
                chars[i * 2 + 0] = "0123456789abcdef"[b >> 4];
                chars[i * 2 + 0] = "0123456789abcdef"[b & 0xF];
            }

            path = new RustStr(AssetDatabase.GUIDToAssetPath(new string(chars)));
        }



        // ReSharper disable InconsistentNaming
        private static readonly NativeCsData.version_mismatch_t version_mismatch = VersionMismatch;
        private static readonly NativeCsData.display_dialog_t display_dialog = DisplayDialog;
        private static readonly NativeCsData.log_error_t log_error = LogError;
        private static readonly NativeCsData.guid_to_asset_path_t guid_to_asset_path = GuidToAssetPath;

        delegate bool vpai_native_entry_point_t(in NativeCsData data);

        struct NativeCsData
        {
            public ulong version;
            public IntPtr version_mismatch;
            // end of version independent part

            // config json info. might not be utf8
            public IntPtr config_ptr;
            public int config_len;
            // config json info. might not be utf8
            public IntPtr display_dialog;
            public IntPtr log_error;
            public IntPtr guid_to_asset_path;


            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void version_mismatch_t();
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate bool display_dialog_t(in RustStr title, in RustStr message, in RustStr ok, in RustStr cancel);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void log_error_t(in RustStr messagePtr);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void guid_to_asset_path_t(in RustGUID guid, out RustStr path);
        }

        struct RustGUID {
            public fixed byte bytes[128/8];
        }

        readonly struct RustStr
        {
            private readonly byte *ptr;
            private readonly IntPtr len;

            public RustStr(string body)
            {
                var bytes = UTF8.GetBytes(body);
                var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                FixedKeep.Add(handle);
                ptr = (byte*)handle.AddrOfPinnedObject();
                len = (IntPtr)bytes.Length;
            }

            public string AsString()
            {
                if ((ulong) len >= int.MaxValue)
                    throw new InvalidOperationException("str too big to be string");
                return UTF8.GetString(ptr, (int)len);
            }

            private static readonly Encoding UTF8 = new UTF8Encoding(false);
        }
        // ReSharper restore InconsistentNaming
    }
}
