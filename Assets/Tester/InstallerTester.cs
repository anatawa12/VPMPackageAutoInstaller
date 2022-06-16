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

        private void OnGUI()
        {
            if (GUILayout.Button("Run Installer"))
                AutoPackageInstaller.DoInstall();
            if (GUILayout.Button("Remove Installer"))
                AutoPackageInstaller.RemoveSelf();
        }
    }
}
