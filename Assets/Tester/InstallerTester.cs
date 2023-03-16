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
        }
    }
}
