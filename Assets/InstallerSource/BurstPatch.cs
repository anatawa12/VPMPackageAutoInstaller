
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor.Compilation;
using UnityAssembly = UnityEditor.Compilation.Assembly;
using MonoAssembly = System.Reflection.Assembly;
using Debug = UnityEngine.Debug;

namespace Anatawa12.VpmPackageAutoInstaller
{
    public static class BurstPatch
    {
        public static void UpdateBurstAssemblyFolders()
        {
            // first, initialize BurstLoader
            try
            {
                // according to comments on burst 1.8.3, this is used internally in Unity so
                // I use way to initialize BurstLoader.
                var assembly = MonoAssembly.Load("Unity.Burst.Editor");
                var type = assembly.GetType("Unity.Burst.Editor.BurstLoader");
                var prop = type.GetProperty("IsDebugging",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Debug.Assert(prop != null, nameof(prop) + " != null");
                prop.GetValue(null);
            }
            catch
            {
                // we couldn't find burst compiler
                return;
            }

            try
            {
                var assemblyList = GetAssemblies();
                var assemblyFolders = new HashSet<string>();

                foreach (var assembly in assemblyList)
                {
                    // skip VPMPackageAutoInstaller.dll
                    if (assembly.GetName().Name == "VPMPackageAutoInstaller") continue;
                    try
                    {
                        var fullPath = Path.GetFullPath(assembly.Location);
                        var assemblyFolder = Path.GetDirectoryName(fullPath);
                        if (!string.IsNullOrEmpty(assemblyFolder))
                        {
                            assemblyFolders.Add(assemblyFolder);
                        }
                    }
                    catch
                    {
                        // ignore
                    }
                }

                // Notify the compiler
                var assemblyFolderList = assemblyFolders.ToList();
                if (VpmPackageAutoInstaller.IsDevEnv())
                {
                    Debug.Log($"VPMPackageAutoInstaller Burst Patch - " +
                              $"Change of list of assembly folders:\n{string.Join("\n", assemblyFolderList)}");
                }

                UpdateAssemblerFolders(assemblyFolderList);
            }
            catch
            {
                // ignore
            }
        }

        static List<MonoAssembly> GetAssemblies()
        {
            var allEditorAssemblies = new List<MonoAssembly>();

            // Filter the assemblies
            var assemblyList = CompilationPipeline.GetAssemblies(AssembliesType.Editor);

            var assemblyNames = new HashSet<string>();
            foreach (var assembly in assemblyList)
                CollectAssemblyNames(assembly, assemblyNames);

            void CollectAssemblyNames(UnityAssembly assembly, HashSet<string> collect)
            {
                if (assembly?.name == null) return;
                if (!collect.Add(assembly.name)) return;

                foreach (var assemblyRef in assembly.assemblyReferences)
                    CollectAssemblyNames(assemblyRef, collect);
            }

            var allAssemblies = new HashSet<MonoAssembly>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assemblyNames.Contains(assembly.GetName().Name))
                {
                    continue;
                }

                CollectAssembly(assembly, allAssemblies);
            }

            void CollectAssembly(MonoAssembly assembly,
                HashSet<MonoAssembly> collect)
            {
                if (!collect.Add(assembly)) return;

                allEditorAssemblies.Add(assembly);

                var referencedAssemblies = assembly.GetReferencedAssemblies();

                foreach (var assemblyName in referencedAssemblies)
                {
                    try
                    {
                        CollectAssembly(MonoAssembly.Load(assemblyName), collect);
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                }
            }

            return allEditorAssemblies;
        }

        private static void UpdateAssemblerFolders(List<string> folders)
        {
            var burstAssembly = MonoAssembly.Load("Unity.Burst");
            var compilerType = burstAssembly.GetType("Unity.Burst.BurstCompiler");
            var sendCommandToCompilerMethod = compilerType.GetMethod("SendCommandToCompiler",
                BindingFlags.Static | BindingFlags.NonPublic,
                null,
                new[] { typeof(string), typeof(string) },
                null);

            Debug.Assert(sendCommandToCompilerMethod != null,
                nameof(sendCommandToCompilerMethod) + " != null");

            sendCommandToCompilerMethod.Invoke(null,
                new object[] { "$update_assembly_folders", $"{string.Join(";", folders)}" });
        }
    }
}
