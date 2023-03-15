using System;
using System.Collections.Generic;
using System.Linq;
using Anatawa12.SimpleJson;
using JetBrains.Annotations;
using Version = SemanticVersioning.Version;

namespace Anatawa12.VpmPackageAutoInstaller.VrcGet
{
    #region manifest

    class VpmDependency
    {
        public Version Version { get; }

        public VpmDependency([NotNull] Version version) =>
            Version = version ?? throw new ArgumentNullException(nameof(version));

        public VpmDependency(JsonObj json) : this(Version.Parse(json.Get("version", JsonType.String)))
        {
        }

        public JsonObj ToJson() => new JsonObj { { "version", Version.ToString() } };
    }

    class VpmLockedDependency
    {
        public Version Version { get; }
        public Dictionary<string, VersionRange> Dependencies { get; }

        public VpmLockedDependency([NotNull] Version version, [NotNull] Dictionary<string, VersionRange> dependencies)
        {
            Version = version ?? throw new ArgumentNullException(nameof(version));
            Dependencies = dependencies ?? throw new ArgumentNullException(nameof(dependencies));
        }

        public VpmLockedDependency(JsonObj json)
        {
            Version = Version.Parse(json.Get("version", JsonType.String));
            Dependencies = new Dictionary<string, VersionRange>();
            var dependencies = json.Get("dependencies", JsonType.Obj, true);
            if (dependencies != null)
            {
                foreach (var (key, value) in dependencies)
                {
                    Dependencies[key] = VersionRange.Parse((string)value);
                }
            }
        }

        public JsonObj ToJson()
        {
            var result = new JsonObj();
            result.Add("version", Version.ToString());
            if (Dependencies.Count != 0)
            {
                var dependencies = new JsonObj();
                result.Add("dependencies", dependencies);
                foreach (var (key, value) in Dependencies)
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
        [NotNull] public string Name { get; }
        [NotNull] public Version Version { get; }
        [NotNull] public string Url { get; }
        [NotNull] public Dictionary<string, VersionRange> VpmDependencies { get; }

        public PackageJson(JsonObj json)
        {
            Json = json;
            Version = Version.Parse(Json.Get("version", JsonType.String));
            Name = Json.Get("name", JsonType.String);
            Url = Json.Get("url", JsonType.String, true) ?? "";
            VpmDependencies = Json.Get("vpmDependencies", JsonType.Obj, true)
                                  ?.ToDictionary(x => x.Item1, x => VersionRange.Parse((string)x.Item2))
                              ?? new Dictionary<string, VersionRange>();
        }
    }

    #endregion

    #region setting

    class UserRepoSetting
    {
        [NotNull] public string LocalPath { get; set; }
        [CanBeNull] public string Name { get; set; }
        [CanBeNull] public string URL { get; set; }

        public UserRepoSetting([NotNull] string localPath, [CanBeNull] string name, [CanBeNull] string url)
        {
            LocalPath = localPath ?? throw new ArgumentNullException(nameof(localPath));
            Name = name;
            URL = url;
        }

        public UserRepoSetting(JsonObj json)
        {
            LocalPath = json.Get("localPath", JsonType.String);
            Name = json.Get("name", JsonType.String, true);
            URL = json.Get("url", JsonType.String, true);
        }

        public JsonObj ToJson()
        {
            var result =  new JsonObj();
            result.Add("localPath", LocalPath);
            if (Name != null) result.Add("name", Name);
            if (URL != null) result.Add("url", URL);
            return result;
        }
    }

    #endregion

    #region repository

    class LocalCachedRepository
    {
        // for vrc-get cache compatibility, this doesn't keep original json
        [CanBeNull] public JsonObj Repo { get; set; }
        [NotNull] public JsonObj Cache { get; set; }
        [CanBeNull] public CreationInfo CreationInfo { get; }
        [CanBeNull] public Description Description { get; }
        [CanBeNull] public VrcGetMeta VrcGet { get; set; }

        public LocalCachedRepository(JsonObj json)
        {
            Repo = json.Get("repo", JsonType.Obj, true);
            Cache = json.Get("cache", JsonType.Obj, true) ?? new JsonObj();
            CreationInfo = json.Get("CreationInfo", JsonType.Obj, true) is JsonObj creationInfo
                ? new CreationInfo(creationInfo)
                : null;
            Description = json.Get("Description", JsonType.Obj, true) is JsonObj description
                ? new Description(description)
                : null;
            VrcGet = json.Get("vrc-get", JsonType.Obj, true) is JsonObj vrcGetMeta
                ? new VrcGetMeta(vrcGetMeta)
                : null;
        }

        public LocalCachedRepository([NotNull] string path, [CanBeNull] string name, [CanBeNull] string url)
        {
            Repo = null;
            Cache = new JsonObj();
            CreationInfo = new CreationInfo
            {
                LocalPath = path,
                URL = url,
                Name = name,
            };
            Description = new Description
            {
                Name = name,
                Type = "JsonRepo",
            };
            VrcGet = null;
        }

        public JsonObj ToJson()
        {
            var result =  new JsonObj();
            if (Repo != null) result.Add("repo", Repo);
            if (Cache.Count != 0) result.Add("cache", Cache);
            if (CreationInfo != null) result.Add("CreationInfo", CreationInfo.ToJson());
            if (Description != null) result.Add("Description", Description.ToJson());
            if (VrcGet != null) result.Add("vrc-get", VrcGet.ToJson());
            return result;
        }
    }

    class CreationInfo
    {
        [CanBeNull] public string LocalPath { get; set; }
        [CanBeNull] public string URL { get; set; }
        [CanBeNull] public string Name { get; set; }

        public CreationInfo()
        {
        }

        public CreationInfo(JsonObj creationInfo)
        {
            LocalPath = creationInfo.Get("localPath", JsonType.String, true);
            URL = creationInfo.Get("url", JsonType.String, true);
            Name = creationInfo.Get("name", JsonType.String, true);
        }

        public JsonObj ToJson()
        {
            var result =  new JsonObj();
            if (LocalPath != null) result.Add("localPath", LocalPath);
            if (URL != null) result.Add("url", URL);
            if (Name != null) result.Add("name", Name);
            return result;
        }
    }

    class Description
    {
        [CanBeNull] public string Name { get; set; }
        [CanBeNull] public string Type { get; set; }

        public Description()
        {
        }

        public Description(JsonObj description)
        {
            Name = description.Get("name", JsonType.String, true);
            Type = description.Get("type", JsonType.String, true);
        }

        public JsonObj ToJson()
        {
            var result =  new JsonObj();
            if (Name != null) result.Add("name", Name);
            if (Type != null) result.Add("type", Type);
            return result;
        }
    }

    class VrcGetMeta
    {
        [CanBeNull] public string Etag { get; set; }

        public VrcGetMeta()
        {
        }

        public VrcGetMeta(JsonObj vrcGetMeta)
        {
            Etag = vrcGetMeta.Get("etag", JsonType.String, true);
        }

        public JsonObj ToJson()
        {
            var result =  new JsonObj();
            if (Etag != null) result.Add("etag", Etag);
            return result;
        }
    }

    #endregion

    #region remote_repo

    class PackageVersions
    {
        [NotNull] public Dictionary<string, PackageJson> Versions { get; set; }

        public PackageVersions()
        {
            Versions = new Dictionary<string, PackageJson>();
        }

        public PackageVersions(JsonObj json) : this()
        {
            var obj = json.Get("versions", JsonType.Obj, true);
            if (obj != null)
            {
                foreach (var (version, packageJson) in obj)
                {
                    Versions[version] = new PackageJson((JsonObj) packageJson);
                }
            }
        }
    }

    #endregion
}
