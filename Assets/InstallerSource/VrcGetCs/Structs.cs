// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;
using System.Linq;
using Anatawa12.SimpleJson;
using JetBrains.Annotations;
using Version = SemanticVersioning.Version;

namespace Anatawa12.VrcGet
{
    #region manifest

    class VpmDependency
    {
        public Version version { get; }

        public VpmDependency([NotNull] Version version) =>
            this.version = version ?? throw new ArgumentNullException(nameof(version));

        public VpmDependency(JsonObj json) : this(Version.Parse(json.Get("version", JsonType.String)))
        {
        }

        public JsonObj ToJson() => new JsonObj { { "version", version.ToString() } };
    }

    class VpmLockedDependency
    {
        public Version version { get; }
        public Dictionary<string, VersionRange> dependencies { get; }

        public VpmLockedDependency([NotNull] Version version, [NotNull] Dictionary<string, VersionRange> dependencies)
        {
            this.version = version ?? throw new ArgumentNullException(nameof(version));
            this.dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        }

        public VpmLockedDependency(JsonObj json)
        {
            version = Version.Parse(json.Get("version", JsonType.String));
            this.dependencies = new Dictionary<string, VersionRange>();
            var dependencies = json.Get("dependencies", JsonType.Obj, true);
            if (dependencies != null)
            {
                foreach (var (key, value) in dependencies)
                {
                    this.dependencies[key] = VersionRange.Parse((string)value);
                }
            }
        }

        public JsonObj ToJson()
        {
            var result = new JsonObj();
            result.Add("version", version.ToString());
            if (dependencies.Count != 0)
            {
                var dependencies = new JsonObj();
                result.Add("dependencies", dependencies);
                foreach (var (key, value) in this.dependencies)
                    dependencies.Add(key, value.ToString());
            }
            return result;
        }
    }

    #endregion

    #region package

    internal class PackageJson
    {
        public JsonObj Json { get; }
        [NotNull] public string name { get; }
        [NotNull] public Version version { get; }
        [NotNull] public string url { get; }
        [NotNull] public Dictionary<string, VersionRange> vpm_dependencies { get; }
        [NotNull] public Dictionary<string, string> legacy_folders { get; } // VPAI
        [NotNull] public Dictionary<string, string> legacy_files { get; } // VPAI

        public PackageJson(JsonObj json)
        {
            Json = json;
            version = Version.Parse(Json.Get("version", JsonType.String));
            name = Json.Get("name", JsonType.String);
            url = Json.Get("url", JsonType.String, true) ?? "";
            vpm_dependencies = Json.Get("vpmDependencies", JsonType.Obj, true)
                                  ?.ToDictionary(x => x.Item1, x => VersionRange.Parse((string)x.Item2))
                              ?? new Dictionary<string, VersionRange>();
            // begin VPAI
            legacy_folders = Json.Get("legacyFolders", JsonType.Obj, true)
                                ?.ToDictionary(x => x.Item1, x => (string)x.Item2)
                            ?? new Dictionary<string, string>();
            legacy_files = Json.Get("legacyFiles", JsonType.Obj, true)
                                  ?.ToDictionary(x => x.Item1, x => (string)x.Item2)
                              ?? new Dictionary<string, string>();
            // end VPAI 
        }
    }

    #endregion

    #region setting

    class UserRepoSetting
    {
        [NotNull] public string local_path { get; set; }
        [CanBeNull] public string name { get; set; }
        [CanBeNull] public string url { get; set; }

        public UserRepoSetting([NotNull] string localPath, [CanBeNull] string name, [CanBeNull] string url)
        {
            local_path = localPath ?? throw new ArgumentNullException(nameof(localPath));
            this.name = name;
            this.url = url;
        }

        public UserRepoSetting(JsonObj json)
        {
            local_path = json.Get("localPath", JsonType.String);
            name = json.Get("name", JsonType.String, true);
            url = json.Get("url", JsonType.String, true);
        }

        public JsonObj ToJson()
        {
            var result =  new JsonObj();
            result.Add("localPath", local_path);
            if (name != null) result.Add("name", name);
            if (url != null) result.Add("url", url);
            return result;
        }
    }

    #endregion

    #region repository

    class LocalCachedRepository
    {
        // for vrc-get cache compatibility, this doesn't keep original json
        [CanBeNull] public JsonObj repo { get; set; }
        [NotNull] public JsonObj cache { get; set; }
        [CanBeNull] public CreationInfo creation_info { get; }
        [CanBeNull] public Description description { get; }
        [CanBeNull] public VrcGetMeta vrc_get { get; set; }

        public LocalCachedRepository(JsonObj json)
        {
            repo = json.Get("repo", JsonType.Obj, true);
            cache = json.Get("cache", JsonType.Obj, true) ?? new JsonObj();
            creation_info = json.Get("CreationInfo", JsonType.Obj, true) is JsonObj creationInfo
                ? new CreationInfo(creationInfo)
                : null;
            this.description = json.Get("Description", JsonType.Obj, true) is JsonObj description
                ? new Description(description)
                : null;
            vrc_get = json.Get("vrc-get", JsonType.Obj, true) is JsonObj vrcGetMeta
                ? new VrcGetMeta(vrcGetMeta)
                : null;
        }

        public LocalCachedRepository([NotNull] string path, [CanBeNull] string name, [CanBeNull] string url)
        {
            repo = null;
            cache = new JsonObj();
            creation_info = new CreationInfo
            {
                local_path = path,
                url = url,
                name = name,
            };
            description = new Description
            {
                name = name,
                type = "JsonRepo",
            };
            vrc_get = null;
        }

        public JsonObj ToJson()
        {
            var result =  new JsonObj();
            if (repo != null) result.Add("repo", repo);
            if (cache.Count != 0) result.Add("cache", cache);
            if (creation_info != null) result.Add("CreationInfo", creation_info.ToJson());
            if (description != null) result.Add("Description", description.ToJson());
            if (vrc_get != null) result.Add("vrc-get", vrc_get.ToJson());
            return result;
        }
    }

    class CreationInfo
    {
        [CanBeNull] public string local_path { get; set; }
        [CanBeNull] public string url { get; set; }
        [CanBeNull] public string name { get; set; }

        public CreationInfo()
        {
        }

        public CreationInfo(JsonObj creationInfo)
        {
            local_path = creationInfo.Get("localPath", JsonType.String, true);
            url = creationInfo.Get("url", JsonType.String, true);
            name = creationInfo.Get("name", JsonType.String, true);
        }

        public JsonObj ToJson()
        {
            var result =  new JsonObj();
            if (local_path != null) result.Add("localPath", local_path);
            if (url != null) result.Add("url", url);
            if (name != null) result.Add("name", name);
            return result;
        }
    }

    class Description
    {
        [CanBeNull] public string name { get; set; }
        [CanBeNull] public string type { get; set; }

        public Description()
        {
        }

        public Description(JsonObj description)
        {
            name = description.Get("name", JsonType.String, true);
            type = description.Get("type", JsonType.String, true);
        }

        public JsonObj ToJson()
        {
            var result =  new JsonObj();
            if (name != null) result.Add("name", name);
            if (type != null) result.Add("type", type);
            return result;
        }
    }

    class VrcGetMeta
    {
        [CanBeNull] public string etag { get; set; }

        public VrcGetMeta()
        {
        }

        public VrcGetMeta(JsonObj vrcGetMeta)
        {
            etag = vrcGetMeta.Get("etag", JsonType.String, true);
        }

        public JsonObj ToJson()
        {
            var result =  new JsonObj();
            if (etag != null) result.Add("etag", etag);
            return result;
        }
    }

    #endregion

    #region remote_repo

    class PackageVersions
    {
        [NotNull] public Dictionary<string, PackageJson> versions { get; set; }

        public PackageVersions()
        {
            versions = new Dictionary<string, PackageJson>();
        }

        public PackageVersions(JsonObj json) : this()
        {
            var obj = json.Get("versions", JsonType.Obj, true);
            if (obj != null)
            {
                foreach (var (version, packageJson) in obj)
                {
                    versions[version] = new PackageJson((JsonObj) packageJson);
                }
            }
        }
    }

    #endregion
}
