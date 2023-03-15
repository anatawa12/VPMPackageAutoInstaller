// the header module only for compiling vpm-package-auto-installer.

// ReSharper disable PossibleNullReferenceException
// ReSharper disable InconsistentNaming

[assembly:System.Reflection.AssemblyVersionAttribute("0.0.0.0")]

namespace UnityEngine
{
    public abstract class Object
    {
        public override bool Equals(object obj) => throw null;
        public override int GetHashCode() => throw null;
        public static bool operator ==(Object a, Object b) => throw null;
        public static bool operator !=(Object a, Object b) => throw null;
    }

    public static class Debug
    {
        public static void LogError(object msg) => throw null;
        public static void Log(object message) => throw null;
        public static void LogException(System.Exception exception) => throw null;
        public static void Assert(bool condition, object message) => throw null;
    }
}
