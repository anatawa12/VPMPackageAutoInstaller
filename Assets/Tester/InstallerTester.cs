using UnityEditor;
using UnityEngine;

namespace Anatawa12.AutoPackageInstaller
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
                AutoPackageInstaller.DoInstall();
            if (GUILayout.Button("Remove Installer"))
                AutoPackageInstaller.RemoveSelf();
            _repoURL = GUILayout.TextField(_repoURL);
            if (GUILayout.Button("Add Repository"))
                Debug.Log($"AddRepo: {VRChatPackageManager.AddPackageRepository(_repoURL)}");
            _packageId = EditorGUILayout.TextField("pkg id", _packageId);
            _packageVersion = EditorGUILayout.TextField("pkg ver", _packageVersion);
            if (GUILayout.Button("Add Package"))
                Debug.Log($"AddPackage: {VRChatPackageManager.AddPackage(_packageId, _packageVersion, callResolver: false)}");
            if (GUILayout.Button("Call Resolver"))
                VRChatPackageManager.CallResolver();
        }
    }
}
