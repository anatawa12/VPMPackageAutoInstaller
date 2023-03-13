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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Anatawa12.SimpleJson;
using UnityEditor;
using UnityEngine;
using Version = SemanticVersioning.Version;

namespace Anatawa12.VpmPackageAutoInstaller
{
       
    // Mini VPM Simulator

    #region VPM

    internal class VpmManifestDependencies
    {
        private readonly JsonObj _body;

        public VpmManifestDependencies(JsonObj body)
        {
            _body = body;
        }

        public string GetVersion(string package) =>
            _body.Get(package, JsonType.Obj, true)
                ?.Get("version", JsonType.String, true);

        public bool NeedsUpdate(string package, string version)
        {
            var foundVersion = GetVersion(package);
            // if version in dependencies is newer, do not update
            return foundVersion == null 
                   || !Version.TryParse(version, out var requestedParsed) 
                   || !Version.TryParse(foundVersion, out var foundParsed) 
                   || foundParsed < requestedParsed;
        }

        public bool AddOrUpdate(string package, string version, bool overrideNewer = false)
        {
            if (!overrideNewer && !NeedsUpdate(package, version)) return false;
            _body.GetOrPut(package, () => new JsonObj(), JsonType.Obj)
                .Put("version", version, JsonType.String);
            return true;
        }
    }

    internal class VpmManifest : IDisposable
    {
        private readonly string _path;
        private readonly JsonObj _body;

        public VpmManifestDependencies Dependencies { get; }
        public VpmManifestDependencies Locked { get; }

        public VpmManifest(string path) : this(path, VRChatPackageManager.ReadJsonOrEmpty(path))
        {
        }

        public VpmManifest(string path, JsonObj body)
        {
            _path = path;
            _body = body;

            Dependencies = new VpmManifestDependencies(body.GetOrPut("dependencies", () => new JsonObj(), JsonType.Obj));
            Locked = new VpmManifestDependencies(body.GetOrPut("locked", () => new JsonObj(), JsonType.Obj));
        }

        // add to dependencies & lock if needed
        public bool AddPackage(string package, string version, bool overrideNewer = false)
        {
            if (overrideNewer)
            {
                Dependencies.AddOrUpdate(package, version, true);
                Locked.AddOrUpdate(package, version, true);
                return true;
            }

            if (!Dependencies.NeedsUpdate(package, version)) return false;
            Dependencies.AddOrUpdate(package, version);

            if (!Locked.NeedsUpdate(package, version)) return false;
            Locked.AddOrUpdate(package, version);

            return true;
        }

        public static VpmManifest Load() => new VpmManifest(VRChatPackageManager.VpmManifestPath);

        public void Save()
        {
            File.WriteAllText(_path, JsonWriter.Write(_body), Encoding.UTF8);
        }

        void IDisposable.Dispose() => Save();
    }

    internal class VpmUserRepository
    {
        public JsonObj Json { get; }
        public string Url { get; }
        public string Name { get; }
        private readonly Lazy<JsonObj> _packages;

        public VpmUserRepository(string url) : this(url,
            new JsonParser(VRChatPackageManager.FetchText(url)).Parse(JsonType.Obj))
        {
        }

        public VpmUserRepository(string url, JsonObj json)
        {
            Url = url;
            Json = json;
            Name = Json.Get("name", JsonType.String);
            _packages = new Lazy<JsonObj>(() => Json.Get("packages", JsonType.Obj, true), false);
        }

        public IEnumerable<string> GetVersions(string package) =>
            _packages.Value?.Get(package, JsonType.Obj, true)
                ?.Get("versions", JsonType.Obj, true)
                ?.Keys
            ?? Array.Empty<string>();
    }

    internal class VpmGlobalSetting : IDisposable
    {
        private readonly string _path;
        private readonly JsonObj _body;

        private readonly List<object> _userRepos;

        public VpmGlobalSetting(string path) : this(path, VRChatPackageManager.ReadJsonOrEmpty(path))
        {
        }

        public VpmGlobalSetting(string path, JsonObj body)
        {
            _path = path;
            _body = body;
            
            _userRepos = body.GetOrPut("userRepos", () => new List<object>(), JsonType.List);
        }

        public bool RepositoryExists(string url) =>
            _userRepos.Any(o => o is JsonObj userRepo && userRepo.Get("url", JsonType.String) == url);

        public bool AddPackageRepository(string url)
        {
            if (RepositoryExists(url)) return false;
            return AddPackageRepository(new VpmUserRepository(url));
        }

        public bool AddPackageRepository(VpmUserRepository repository)
        {
            // find existing
            if (RepositoryExists(repository.Url)) return false;

            // generate local repo path
            var localRepoPath = Path.Combine(VRChatPackageManager.GlobalReposFolder, Guid.NewGuid() + ".json");
            while (File.Exists(localRepoPath))
                localRepoPath = Path.Combine(VRChatPackageManager.GlobalReposFolder, Guid.NewGuid() + ".json");

            var repoName = repository.Name;

            // create local repo info
            File.WriteAllText(localRepoPath, JsonWriter.Write(new JsonObj
            {
                { "repo", repository.Json },
                {
                    "CreationInfo", new JsonObj
                    {
                        { "localPath", localRepoPath },
                        { "url", repository.Url },
                        { "name", repoName },
                    }
                },

                {
                    "Description", new JsonObj
                    {
                        { "name", repoName },
                        { "type", "JsonRepo" },
                    }
                },
            }), Encoding.UTF8);

            // update settings
            _userRepos.Add(new JsonObj
            {
                {"localPath", localRepoPath},
                {"url", repository.Url},
                {"name", repoName},
            });

            return true;
        }

        public void Save()
        {
            File.WriteAllText(_path, JsonWriter.Write(_body), Encoding.UTF8);
        }

        public static VpmGlobalSetting Load() => new VpmGlobalSetting(VRChatPackageManager.GlobalSettingPath);

        void IDisposable.Dispose() => Save();
    }

    internal class VRChatPackageManager
    {
        public static string GlobalFoler = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VRChatCreatorCompanion");

        public static string GlobalSettingPath = Path.Combine(GlobalFoler, "settings.json");
        public static string GlobalReposFolder = Path.Combine(GlobalFoler, "Repos");
        public static string ProjectFolder = Directory.GetCurrentDirectory();
        public static string VpmManifestPath = Path.Combine(ProjectFolder, "Packages", "vpm-manifest.json");

        public static void CallResolver()
        {
            try
            {
                // first, call VPMProjectManifest.Resolve
                {
                    var asm = Assembly.Load("vpm-core-lib");
                    var type = asm.GetType("VRC.PackageManagement.Core.Types.Packages.VPMProjectManifest");
                    var method = type.GetMethod("Resolve", BindingFlags.Public | BindingFlags.Static, null,
                        new[] { typeof(string) }, null);
                    method.Invoke(null, new object[] { ProjectFolder });
                }
                // first, call UnityEditor.PackageManager.Client.Resolve
                {
                    var method = typeof(UnityEditor.PackageManager.Client)
                        .GetMethod("Resolve", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    if (method != null)
                        method.Invoke(null, null);
                }
                return;
            }
            catch (Exception e)
            {
                Debug.LogError("Invoking VPMProjectManifest.Resolve failed. ");
                Debug.LogError(e);
            }

            SessionState.SetBool("PROJECT_LOADED", false);
        }

        public static JsonObj ReadJsonOrEmpty(string path)
        {
            try
            {
                return new JsonParser(File.ReadAllText(path, Encoding.UTF8)).Parse(JsonType.Obj);
            }
            catch (FileNotFoundException)
            {
                return new JsonObj();
            }
        }

        public static string FetchText(string url)
        {
            var response = new HttpClient().GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                throw new IOException($"Getting {url} failed");

            return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        }
    }

    #endregion
}
