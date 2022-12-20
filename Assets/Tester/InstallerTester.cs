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

        private void OnGUI()
        {
            if (GUILayout.Button("Run Installer"))
                AutoPackageInstaller.DoInstall();
            if (GUILayout.Button("Remove Installer"))
                AutoPackageInstaller.RemoveSelf();
            _repoURL = GUILayout.TextField(_repoURL);
            if (GUILayout.Button("Add Repository"))
                Debug.Log($"AddRepo: {VRChatPackageManager.AddPackageRepository(_repoURL)}");
        }
    }
}
