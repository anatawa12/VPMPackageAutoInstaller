// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;
using System.Linq;
using Anatawa12.SimpleJson;
using JetBrains.Annotations;
using Version = SemanticVersioning.Version;
// ReSharper disable ArrangeThisQualifier
// ReSharper disable LocalVariableHidesMember

namespace Anatawa12.VrcGet
{
    #region manifest

    class VpmDependency
    {
        public DependencyRange version { get; }

        public VpmDependency([NotNull] Version version) =>
            this.version = new DependencyRange(version ?? throw new ArgumentNullException(nameof(version)));

        public VpmDependency([NotNull] DependencyRange version) =>
            this.version = version;

        public VpmDependency(JsonObj json) : this(DependencyRange.Parse(json.Get("version", JsonType.String)))
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
        [NotNull] public Dictionary<string, string> legacy_folders { get; }
        [NotNull] public Dictionary<string, string> legacy_files { get; }
        [NotNull] public string[] legacy_packages { get; }

        public PackageJson(JsonObj json)
        {
            Json = json;
            version = Version.Parse(Json.Get("version", JsonType.String));
            name = Json.Get("name", JsonType.String);
            url = Json.Get("url", JsonType.String, true) ?? "";
            vpm_dependencies = Json.Get("vpmDependencies", JsonType.Obj, true)
                                  ?.ToDictionary(x => x.Item1, x => VersionRange.Parse((string)x.Item2))
                              ?? new Dictionary<string, VersionRange>();
            legacy_folders = Json.Get("legacyFolders", JsonType.Obj, true)
                                ?.ToDictionary(x => x.Item1, x => (string)x.Item2)
                            ?? new Dictionary<string, string>();
            legacy_files = Json.Get("legacyFiles", JsonType.Obj, true)
                                  ?.ToDictionary(x => x.Item1, x => (string)x.Item2)
                              ?? new Dictionary<string, string>();
            legacy_packages = Json.Get("legacyPackages", JsonType.List, true)
                                  ?.Cast<string>().ToArray()
                              ?? Array.Empty<string>();
        }
    }

    #endregion

    #region setting

    class UserRepoSetting
    {
        [NotNull] public string local_path { get; set; }
        [CanBeNull] public string name { get; set; }
        [CanBeNull] public string url { get; set; }
        [CanBeNull] public string id { get; set; }
        [NotNull] public Dictionary<string, string> headers { get; set; }

        public UserRepoSetting([NotNull] string localPath, [CanBeNull] string name, [CanBeNull] string url, string id)
        {
            local_path = localPath ?? throw new ArgumentNullException(nameof(localPath));
            this.name = name;
            this.url = url;
            this.id = id;
            this.headers = new Dictionary<string, string>();
        }

        public UserRepoSetting(JsonObj json)
        {
            local_path = json.Get("localPath", JsonType.String);
            name = json.Get("name", JsonType.String, true);
            url = json.Get("url", JsonType.String, true);
            id = json.Get("id", JsonType.String, true);
            headers = JsonUtils.ToDictionary(json.Get("versions", JsonType.Obj, true), x => (string)x);
        }

        public JsonObj ToJson()
        {
            var result =  new JsonObj();
            result.Add("localPath", local_path);
            if (name != null) result.Add("name", name);
            if (url != null) result.Add("url", url);
            if (id != null) result.Add("id", id);
            return result;
        }
    }

    #endregion

    #region repo_cache

    class LocalCachedRepository
    {
        // for vrc-get cache compatibility, this doesn't keep original json
        [NotNull] Repository _repo { get; set; }
        [NotNull] Dictionary<string, string> _headers { get; set; }
        [CanBeNull] public VrcGetMeta vrc_get { get; set; }

        public LocalCachedRepository(JsonObj json)
        {
            _repo = new Repository(json.Get("repo", JsonType.Obj, true));
            _headers = JsonUtils.ToDictionary(json.Get("headers", JsonType.Obj, true), x => (string)x);
            vrc_get = json.Get("vrc-get", JsonType.Obj, true) is JsonObj vrcGetMeta ? new VrcGetMeta(vrcGetMeta) : null;
        }

        public LocalCachedRepository([NotNull] Repository repo, [NotNull] Dictionary<string, string> headers)
        {
            this._repo = repo ?? throw new ArgumentNullException(nameof(repo));
            this._headers = headers ?? throw new ArgumentNullException(nameof(headers));
            vrc_get = null;
        }

        public JsonObj ToJson()
        {
            var result =  new JsonObj();
            result.Add("repo", _repo.ToJson());
            result.Add("headers", headers().ToJson(x => x));
            if (vrc_get != null) result.Add("vrc-get", vrc_get.ToJson());
            return result;
        }

        [NotNull] public Dictionary<string, string> headers() => this._headers;
        [NotNull] public Repository repo() => this._repo;

        public void set_repo(Repository repo)
        {
            if (this.id() is string id) repo.set_id_if_none(id);
            if (this.url() is string url) repo.set_url_if_none(url);
            this._repo = repo;
        }

        [CanBeNull] public string url() => repo().url();
        [CanBeNull] public string id() => repo().id();
        [CanBeNull] public string name() => repo().name();
        [NotNull] public IEnumerable<PackageJson> get_version_of(string package) => repo().get_versions_of(package);
        [CanBeNull] public IEnumerable<PackageVersions> get_packages() => repo().get_packages();
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

    #region repository

    class Repository
    {
        private JsonObj actual;
        private ParsedRepository parsed;
        
        public Repository(JsonObj cache)
        {
            this.parsed = new ParsedRepository(cache);
            this.actual = cache;
        }

        public void set_id_if_none(string value){
            if (this.parsed.id == null) {
                this.parsed.id = value;
                this.actual.Put("id", value, JsonType.String);
            }
        }

        public void set_url_if_none(string value){
            if (this.parsed.url == null) {
                this.parsed.url = value;
                this.actual.Put("url", value, JsonType.String);
                set_id_if_none(value);
            }
        }

        [CanBeNull] public string url() => this.parsed.url;

        [CanBeNull] public string id() => this.parsed.id;

        [CanBeNull] public string name() => this.parsed.name;

        [NotNull]
        public IEnumerable<PackageJson> get_versions_of(string package) {
            this.parsed.packages.TryGetValue(package, out var value);
            if (value == null) return Array.Empty<PackageJson>();
            return value.versions.Values;
        }

        public IEnumerable<PackageVersions> get_packages()
        {
            return this.parsed.packages.Values;
        }

        public JsonObj ToJson()
        {
            return actual;
        }

        struct ParsedRepository {
            [CanBeNull] public string name;
            [CanBeNull] public string url;
            [CanBeNull] public string id;
            [NotNull] public Dictionary<String, PackageVersions> packages;

            public ParsedRepository(JsonObj json)
            {
                this.name = json.Get("name", JsonType.String, true);
                this.url = json.Get("url", JsonType.String, true);
                this.id = json.Get("id", JsonType.String, true);
                this.packages = JsonUtils.ToDictionary(json.Get("packages", JsonType.Obj, true),
                    x => new PackageVersions((JsonObj)x));
            }
        }
    }

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

    static class JsonUtils
    {
        public static Dictionary<string, T> ToDictionary<T>(JsonObj obj, Func<object, T> parser)
        {
            var result = new Dictionary<string, T>();
            if (obj != null)
                foreach (var (key, value) in obj)
                    result[key] = parser(value);
            return result;
        }

        public static JsonObj ToJson<T>(this IDictionary<string, T> self, Func<T, object> toJson)
        {
            var obj = new JsonObj();
            foreach (var (key, value) in self)
                obj.Add(key, toJson(value));
            return obj;
        }
    }
}
