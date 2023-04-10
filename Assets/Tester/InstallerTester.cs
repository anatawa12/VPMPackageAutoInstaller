using System;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace Anatawa12.VpmPackageAutoInstaller
{
    public class InstallerTester : EditorWindow
    {
        [MenuItem("Window/InstallerTester")]
        public static void Open()
        {
            GetWindow<InstallerTester>();
        }

        private string _repoURL = "https://vpm.anatawa12.com/vpm.json";
        private string _packageId = "com.anatawa12.custom-localization-for-editor-extension";
        private string _packageVersion = "0.2.0";

        private void OnGUI()
        {
            if (GUILayout.Button("Run Installer"))
                VpmPackageAutoInstaller.DoInstall();
            if (GUILayout.Button("Remove Installer"))
                VpmPackageAutoInstaller.RemoveSelf();
            _repoURL = GUILayout.TextField(_repoURL);
            _packageId = EditorGUILayout.TextField("pkg id", _packageId);
            _packageVersion = EditorGUILayout.TextField("pkg ver", _packageVersion);
            if (GUILayout.Button("Call Resolver"))
                VpmPackageAutoInstaller.ResolveUnityPackageManger();
            if (GUILayout.Button("Try Load"))
            {
                var path = AssetDatabase.GUIDToAssetPath("e22ab70e285a450ab4bca5c1bddc0bae");
                if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("lib not found");
                using (var lib = LibLoader.LibLoader.LoadLibrary(path))
                {
                    var ptr = lib.GetAddress("vpai_native_entry_point");
                    Debug.Log($"found vpai_native_entry_point: {ptr}");

                    void VersionMismatch()
                    {
                        Debug.Log("version mismatch");
                    }

                    NativeCsData.VersionMismatchDelegate @delegate = VersionMismatch;
                    NativeCsData data = new NativeCsData()
                    {
                        version = 0,
                        version_mismatch = Marshal.GetFunctionPointerForDelegate(@delegate),
                    };

                    unsafe
                    {
                        var nativeEntryPoint = Marshal.GetDelegateForFunctionPointer<vpai_native_entry_point_t>(ptr);
                        nativeEntryPoint(&data);
                    }
                }
            }
        }
        
    }

    unsafe delegate bool vpai_native_entry_point_t(NativeCsData* data);

    struct NativeCsData
    {
        public ulong version;
        public IntPtr version_mismatch;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void VersionMismatchDelegate();
    }
}
