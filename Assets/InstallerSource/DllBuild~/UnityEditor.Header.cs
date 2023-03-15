// the header module only for compiling vpm-package-auto-installer.

// ReSharper disable PossibleNullReferenceException
// ReSharper disable InconsistentNaming

[assembly:System.Reflection.AssemblyVersionAttribute("0.0.0.0")]

namespace UnityEditor
{
    namespace PackageManager
    {
        public class Client
        {
            internal void Resolve() => throw null;
        }
    }

    public static class EditorUtility
    {
        public static void SetDirty(UnityEngine.Object obj) => throw null;
        public static bool DisplayDialog(string subject, string message, string ok) => throw null;
        public static bool DisplayDialog(string subject, string message, string ok, string cancel) => throw null;
    }

    public class InitializeOnLoadAttribute : System.Attribute
    {
    }

    public static class AssetDatabase
    {
        public static void Refresh() => throw null;
        public static string GUIDToAssetPath(string guid) => throw null;
        public static T LoadAssetAtPath<T>(string path) where T : UnityEngine.Object => throw null;
        public static string GetAssetPath(UnityEngine.Object asset) => throw null;
    }

    public static class SessionState
    {
        public static void SetBool(string prop, bool value) => throw null;
    }

    public static class BuildPipeline
    {
        public static BuildTargetGroup GetBuildTargetGroup(BuildTarget activeBuildTarget) => throw null;
    }

    public enum BuildTarget
    {
    }

    public enum BuildTargetGroup
    {
    }

    public static class EditorUserBuildSettings
    {
        public static BuildTarget activeBuildTarget => throw null;
    }

    public static class PlayerSettings
    {
        public static string GetScriptingDefineSymbolsForGroup(BuildTargetGroup buildTargetGroup) => throw null;
    }
}

namespace UnityEditor.Compilation
{
    public class Assembly
    {
        public string name => throw null;
        public Assembly[] assemblyReferences => throw null;
    }

    public static class CompilationPipeline
    {
        public static Assembly[] GetAssemblies(AssembliesType assembliesType) => throw null;
    }

    public enum AssembliesType
    {
        Editor,
        Player,
        PlayerWithoutTestAssemblies,
    }
}
