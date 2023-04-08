// ReSharper disable InconsistentNaming
// ReSharper disable ArrangeThisQualifier
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Anatawa12.SimpleJson;
using JetBrains.Annotations;
using UnityEngine;
using static Anatawa12.VrcGet.ModStatics;
using Version = SemanticVersioning.Version;
// ReSharper disable ParameterHidesMember

namespace Anatawa12.VrcGet
{
    sealed partial class Environment
    {
        [CanBeNull] private readonly HttpClient http;
        [NotNull] private readonly string global_dir;
        [NotNull] private readonly JsonObj settings;
        [NotNull] private readonly RepoHolder repo_cache;
        [NotNull] private readonly List<(string, PackageJson)> user_packages;
        [NotNull] public readonly List<(string path, string url)> PendingRepositories = new List<(string, string)>(); // VPAI
        private bool settings_changed;

        private Environment([CanBeNull] HttpClient http, [NotNull] string globalDir, [NotNull] JsonObj settings, [NotNull] RepoHolder repoCache, List<(string, PackageJson)> userPackages, bool settingsChanged)
        {
            this.http = http;
            global_dir = globalDir;
            this.settings = settings;
            repo_cache = repoCache;
            user_packages = userPackages;
            settings_changed = settingsChanged;
        }

        public static async Task<Environment> load_default(HttpClient http)
        {
            // for macOS, might be changed in .NET 7
            var folder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            folder = Path.Combine(folder, "VRChatCreatorCompanion");

            //Debug.Log($"initializing Environment with config folder {folder}");

            return new Environment
            (
                http: http,
                settings: await load_json_or_default(Path.Combine(folder, "settings.json"), x => x),
                globalDir: folder,
                repoCache: new RepoHolder(http),
                userPackages: new List<(string, PackageJson)>(),
                settingsChanged: false
            );
        }

        public async Task load_package_infos()
        {
            await this.repo_cache.load_repos(await this.get_repo_sources());
            this.update_user_repo_id();
            await this.load_user_package_infos();
        }

        void update_user_repo_id() {
            var user_repos = this.get_user_repos();
            if (user_repos.len() == 0)
                return;

            var json = this.settings.Get("userRepos", JsonType.List);

            // update id field
            for (var i = 0; i < user_repos.Count; i++)
            {
                var repo = user_repos[i];
                var loaded = this.repo_cache.get_repo(repo.local_path);
                System.Diagnostics.Debug.Assert(loaded != null, nameof(loaded) + " != null");
                var id = loaded.id();
                if (id != repo.id) {
                    repo.id = id;

                    json[i] = repo.ToJson();
                    this.settings_changed = true;
                }
            }
        }

        async Task load_user_package_infos()
        {
            var self = this;
            self.user_packages.Clear();
            foreach (var x in self.get_user_package_folders())
            {
                var package_json = await load_json_or_else(Path.Combine(x, "package.json"), y => new PackageJson(y),
                    () => null);
                if (package_json != null)
                {
                    self.user_packages.Add((x, package_json));
                }
            }
        }


        [NotNull]
        public string get_repos_dir() => Path.Combine(global_dir, "Repos");

        public PackageInfo? find_package_by_name([NotNull] string package, VersionSelector version)
        {
            var versions = find_packages(package);

            versions.RemoveAll(x => !version(x.version()));
            versions.Sort((x, y) => y.version().CompareTo(x.version()));

            return versions.Count != 0 ? versions[0] as PackageInfo? : null;
        }

        [ItemNotNull]
        public async Task<List<RepoSource>> get_repo_sources()
        {
            // collect user repositories
            var reposBase = get_repos_dir();
            var userRepos = get_user_repos();

            var userRepoFileNames = new HashSet<string>();
            userRepoFileNames.Add("vrc-curated.json");
            userRepoFileNames.Add("vrc-official.json");

            //[CanBeNull]
            string RelativeFileName(string path, string @base)
            {
                var dirName = Path.GetDirectoryName(path)
                    ?.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                return dirName != @base ? null : Path.GetFileName(path);
            }

            // normalize
            reposBase = reposBase.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            userRepos
                .Select(x => RelativeFileName(x.local_path, reposBase))
                .Where(x => x == null)
                .AddAllTo(userRepoFileNames);

            IEnumerable<string> TryEnumerateFiles(string path)
            {
                try
                {
                    return Directory.EnumerateFiles(reposBase);
                }
                catch (DirectoryNotFoundException)
                {
                    return Array.Empty<string>();
                }
            }

            var undefinedRepos = TryEnumerateFiles(reposBase)
                .Where(x => !userRepoFileNames.Contains(x))
                .Where(x => x.EndsWith(".json", StringComparison.Ordinal))
                .Select(x => new UndefinedSource(Path.Combine(reposBase, x)));

            var definedSources = PreDefinedRepoSource.Sources.Select(x =>
                new PreDefinedRepoSource(x, Path.Combine(this.get_repos_dir(), x.file_name)));
            var userRepoSources = userRepos.Select(x => new UserRepoSource(x));

            return await Task.Run(() =>
                undefinedRepos.Concat<RepoSource>(definedSources).Concat(userRepoSources).ToList());
        }

        [ItemNotNull]
        public LocalCachedRepository[] get_repos() => repo_cache.get_repos();

        [ItemNotNull]
        public IEnumerable<(string, LocalCachedRepository)> get_repo_with_path() => repo_cache.get_repo_with_path();


        [ItemNotNull]
        public List<PackageInfo> find_packages([NotNull] string package)
        {
            var list = new List<PackageInfo>();

            list.AddRange(
                this.get_repos()
                    .SelectMany(repo => repo.get_version_of(package).Select(pkg => (pkg, repo)))
                    .Select((pair) => PackageInfo.remote(pair.pkg, pair.repo)));

            // user package folders
            foreach (var (path, package_json) in this.user_packages)
            {
                if (package_json.name == package) {
                    list.Add(PackageInfo.local(package_json, path));
                }
            }

            return list;
        }
        
        [ItemNotNull]
        public List<PackageJson> find_whole_all_packages([NotNull] Func<PackageJson, bool> filter)
        {
            var list = new List<PackageJson>();

            //[CanBeNull] // C# 9.0
            PackageJson GetLatest(PackageVersions versions) =>
                versions
                    .versions
                    .Values
                    .Where(x => !x.version.IsPreRelease)
                    .MaxBy(x => x.version);

            this.get_repos()
                    .SelectMany(repo => repo.get_packages())
                    .Select(GetLatest)
                    .Where(x => x != null)
                    .Where(filter)
                .AddAllTo(list);

            // user package folders
            foreach (var (_, package_json) in this.user_packages) {
                if (!package_json.version.IsPreRelease && filter(package_json)) {
                    list.Add(package_json);
                }
            }

            list.Sort((x, y) => y.version.CompareTo(x.version));

            return list.DistinctBy(x => (Name: x.name, Version: x.version)).ToList();
        }

        public async Task add_package([NotNull] PackageInfo package, [NotNull] string target_packages_folder)
        {
            await AddPackage.add_package(this.global_dir, this.http, package, target_packages_folder);
        }


        [ItemNotNull]
        [NotNull]
        public List<UserRepoSetting> get_user_repos() =>
            settings.Get("userRepos", JsonType.List, true)
                ?.Cast<JsonObj>()
                ?.Select(x => new UserRepoSetting(x))
                ?.ToList() ?? new List<UserRepoSetting>();

        [ItemNotNull]
        [NotNull]
        List<string> get_user_package_folders() =>
            settings.Get("userPackageFolders", JsonType.List, true)?.Cast<string>()?.ToList() ?? new List<string>();

        void add_user_repo([NotNull] UserRepoSetting repo)
        {
            settings.GetOrPut("userRepos", () => new List<object>(), JsonType.List)
                .Add(repo.ToJson());
            settings_changed = true;
        }

        public async Task add_remote_repo([NotNull] string url, [CanBeNull] string name, [CanBeNull] Dictionary<string, string> headers)
        {
            if (get_user_repos().Any(x => x.url == url))
                throw new VrcGetException("Already Added");
            if (http == null)
                throw new OfflineModeException();


            var response = await download_remote_repository(http, url, headers, null);
            Debug.Assert(response != null, nameof(response) + " != null");
            var (remote_repo, etag) = response.Value;
            var localPath = Path.Combine(get_repos_dir(), $"{Guid.NewGuid()}.json");

            var repo_name = name ?? remote_repo.name();
            var repo_id = remote_repo.id();

            if (get_user_repos().Any(x => x.id == repo_id))
                throw new VrcGetException("Already Added");

            var local_cache = new LocalCachedRepository(remote_repo, headers ?? new Dictionary<string, string>());
            // set etag
            if (etag != null) {
                if (local_cache.vrc_get == null)
                    local_cache.vrc_get = new VrcGetMeta();
                local_cache.vrc_get.etag = etag;
            }
            await write_repo(localPath, local_cache);

            add_user_repo(new UserRepoSetting(localPath, repo_name, url, repo_id));
        }

        #region VPAI

        public async Task AddPendingRepository([NotNull] string url, [CanBeNull] string name, [CanBeNull] Dictionary<string, string> headers)
        {
            if (get_user_repos().Any(x => x.url == url))
                return; // allow already added
            if (http == null)
                throw new OfflineModeException();

            var response = await download_remote_repository(http, url, headers, null);
            Debug.Assert(response != null, nameof(response) + " != null");
            var (remote_repo, etag) = response.Value;

            var localCache = new LocalCachedRepository(remote_repo, headers ?? new Dictionary<string, string>());
            // set etag
            if (etag != null) {
                if (localCache.vrc_get == null)
                    localCache.vrc_get = new VrcGetMeta();
                localCache.vrc_get.etag = etag;
            }
            var localPath = Path.Combine(get_repos_dir(), $"{Guid.NewGuid()}.json");
            repo_cache.AddRepository(localPath, localCache);
            PendingRepositories.Add((localPath, remote_repo.url()));
        }

        public async Task SavePendingRepositories()
        {
            for (var i = PendingRepositories.Count - 1; i >= 0; i--)
            {
                var (localPath, _) = PendingRepositories[i];
                PendingRepositories.RemoveAt(i);

                var localCache = repo_cache.get_repo(localPath);
                System.Diagnostics.Debug.Assert(localCache != null, nameof(localCache) + " != null");
                await write_repo(localPath, localCache);
                add_user_repo(new UserRepoSetting(localPath, localCache.name(), localCache.url(), localCache.id()));                
            }
        }

        #endregion

        public void add_local_repo([NotNull] string path, [CanBeNull] string name)
        {
            if (get_user_repos().Any(x => x.local_path == path))
                throw new VrcGetException("Already Added");

            add_user_repo(new UserRepoSetting(path, name, null, null));
        }
    }

    readonly struct PackageInfo
    {
        [NotNull] private readonly PackageJson _packageJson;
        [NotNull] private readonly object _info;

        private PackageInfo([NotNull] PackageJson packageJson, [NotNull] object info)
        {
            _packageJson = packageJson;
            _info = info;
        }

        [NotNull] public PackageJson package_json() => _packageJson;

        public static PackageInfo remote(PackageJson json, LocalCachedRepository repo) => new PackageInfo(json, repo);
        public static PackageInfo local(PackageJson json, string path) => new PackageInfo(json, path);

        public string name()  => this.package_json().name;
        public Version version()  => this.package_json().version;
        public Dictionary<string, VersionRange> vpm_dependencies()  => this.package_json().vpm_dependencies;

        // cs impl
        public bool is_remote() => _info is LocalCachedRepository;
        public LocalCachedRepository remote() => (LocalCachedRepository)_info;
        public string local() => (string)_info;
    }

    interface RepoSource
    {
        Task<LocalCachedRepository> VisitLoadRepo([CanBeNull] HttpClient client);
        string file_path();
    }

    class PreDefinedRepoSource : RepoSource
    {
        public Information Info { get; }
        public string path { get; }

        public PreDefinedRepoSource(Information info, string path)
        {
            Info = info;
            this.path = path;
        }

        public Task<LocalCachedRepository> VisitLoadRepo(HttpClient client) => RepoHolder.LoadPreDefinedRepo(client, this);

        public string file_path() => path;

        public readonly struct Information
        {
            public string file_name { get; }
            public string url { get; }
            public string name { get; }

            private Information(string fileName, string url, string name)
            {
                file_name = fileName;
                this.url = url;
                this.name = name;
            }

            // ReSharper disable MemberHidesStaticFromOuterClass
            public static readonly Information Official = new Information("vrc-official.json",
                "https://packages.vrchat.com/official?download", "Official");
            public static readonly Information Curated = new Information("vrc-curated.json",
                "https://packages.vrchat.com/curated?download", "Curated");
            // ReSharper restore MemberHidesStaticFromOuterClass
        }

        public static readonly Information Official = Information.Official;
        public static readonly Information Curated = Information.Curated;

        public static readonly Information[] Sources = { Official, Curated };
    }

    class UserRepoSource : RepoSource
    {
        public UserRepoSetting Setting;

        public UserRepoSource(UserRepoSetting setting)
        {
            Setting = setting;
        }

        public Task<LocalCachedRepository> VisitLoadRepo(HttpClient client) => RepoHolder.LoadUserRepo(client, this);

        public string file_path() => Setting.local_path;
    }

    class UndefinedSource : RepoSource
    {
        public string Path;

        public UndefinedSource(string path)
        {
            Path = path;
        }

        public Task<LocalCachedRepository> VisitLoadRepo(HttpClient client) => RepoHolder.LoadUndefinedRepo(client, this);

        public string file_path() => Path;
    }

    static partial class ModStatics
    {
        public static async Task update_from_remote([CanBeNull] HttpClient client, [NotNull] string path,
            [NotNull] LocalCachedRepository repo)
        {
            var remoteURL = repo.url();
            if (remoteURL == null) return;

            var foundEtag = repo.vrc_get?.etag;
            try
            {

                var result = await download_remote_repository(client, remoteURL, repo.headers(), foundEtag);
                if (result != null)
                {
                    var (remoteRepo, etag) = result.Value;
                    repo.set_repo(remoteRepo);
                    // set etag
                    if (etag != null)
                    {
                        if (repo.vrc_get == null) repo.vrc_get = new VrcGetMeta();
                        repo.vrc_get.etag = etag;
                    }
                    else
                    {
                        if (repo.vrc_get != null) repo.vrc_get.etag = null;
                    }
                }
                else
                {
                    //Debug.Log($"cache matched downloading {remoteURL}");
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"fetching remote repo '{remoteURL}'");
                Debug.LogException(e);
            }

            try
            {
                await write_repo(path, repo);
            }
            catch (Exception e)
            {
                Debug.LogError($"writing local repo '{path}'");
                Debug.LogException(e);
            }
        }

        public static async Task write_repo([NotNull] string path, [NotNull] LocalCachedRepository repo)
        {
            await Task.Run(() =>
            {
                var dir = Path.GetDirectoryName(path);
                Debug.Assert(dir != null, nameof(dir) + " != null");
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonWriter.Write(repo.ToJson()));
            });
        }

        public static async Task<(Repository, string)?> download_remote_repository(
            HttpClient client,
            string url,
            [CanBeNull] IDictionary<string, string> headers,
            [CanBeNull] string etag
        )
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (etag != null)
            {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
            }

            if (headers != null)
                foreach (var (key, value) in headers)
                    request.Headers.TryAddWithoutValidation(key, value);

            var response = await client.SendAsync(request);
            if (etag != null && response.StatusCode == HttpStatusCode.NotModified)
                return null;
            response.EnsureSuccessStatusCode();

            var newEtag = response.Headers.ETag?.ToString();

            var content = await response.Content.ReadAsStringAsync();
            var repo = new Repository(new JsonParser(content).Parse(JsonType.Obj));
            repo.set_url_if_none(url);
            return (repo, newEtag);
        }
    }

    partial class Environment {
        public Task<bool> remove_repo(Func<UserRepoSetting, bool> condition)
        {
            var removes = get_user_repos()
                .Select((x, i) => (x, i))
                .Where(x => condition(x.x))
                .ToList();
            removes.Reverse();
            if (removes.Count == 0) return Task.FromResult(false);

            var userRepos = settings.Get("userRepos", JsonType.List);
            for (var i = 0; i < removes.Count; i++)
                userRepos.RemoveAt(removes[i].i);

            foreach (var (x, _) in removes)
                File.Delete(x.local_path);
            return Task.FromResult(true);
        }

        public async Task save()
        {
            if (!settings_changed) return;

            await Task.Run(() =>
            {
                Directory.CreateDirectory(global_dir);
                File.WriteAllText(Path.Combine(global_dir, "settings.json"), JsonWriter.Write(settings));
                settings_changed = false;
            });
        }
    }

    #region vpm manifest

    internal class VpmManifest
    {
        private readonly JsonObj _body;
        private readonly Dictionary<string, VpmDependency> _dependencies;
        private readonly Dictionary<string, VpmLockedDependency> _locked;
        private bool _changed;

        public IReadOnlyDictionary<string, VpmDependency> dependencies() => _dependencies;
        public IReadOnlyDictionary<string, VpmLockedDependency> locked() => _locked;

        public VpmManifest(JsonObj body)
        {
            _body = body;

            _dependencies = body.Get("dependencies", JsonType.Obj, true)
                               ?.ToDictionary(x => x.Item1, x => new VpmDependency((JsonObj)x.Item2))
                           ?? new Dictionary<string, VpmDependency>();
            _locked = body.Get("locked", JsonType.Obj, true)
                         ?.ToDictionary(x => x.Item1, x => new VpmLockedDependency((JsonObj)x.Item2))
                     ?? new Dictionary<string, VpmLockedDependency>();
        }

        public void add_dependency(string name, VpmDependency dependency)
        {
            _dependencies[name] = dependency;
            add_value("dependencies", name, dependency.ToJson());
        }

        public void add_locked(string name, VpmLockedDependency dependency)
        {
            _locked[name] = dependency;
            add_value("locked", name, dependency.ToJson());
        }

        private void add_value(string category, string name, JsonObj jsonObj)
        {
            _body.GetOrPut(category, () => new JsonObj(), JsonType.Obj).Put(name, jsonObj, JsonType.Obj);
            _changed = true;
        }

        public async Task SaveTo(string file)
        {
            if (!_changed) return;
            await Task.Run(() => { File.WriteAllText(file, JsonWriter.Write(_body)); });
            _changed = false;
        }
    }

    #endregion

    sealed partial class UnityProject
    {
        private readonly string project_dir;
        public readonly VpmManifest manifest; // VPAI: public
        private readonly List<(string dirName, PackageJson manifest)> unlocked_packages;

        private UnityProject(string project_dir, VpmManifest manifest, List<(string, PackageJson)> unlockedPackages)
        {
            this.project_dir = project_dir;
            this.manifest = manifest;
            this.unlocked_packages = unlockedPackages;
        }

        public static async Task<UnityProject> find_unity_project([NotNull] string unityProject)
        {
            // removed find support
            var unityFound = unityProject; //?? findUnityProjectPath();
            var packages = Path.Combine(unityFound, "Packages");

            var manifest = Path.Combine(packages, "Packages/vpm-manifest.json");
            var vpmManifest = new VpmManifest(await load_json_or_default(manifest, x => x));

            var unlockedPackages = new List<(string, PackageJson)>();

            foreach (var dir in await Task.Run(() => Directory.GetDirectories(packages)))
            {
                var read = await try_read_unlocked_package(dir, Path.Combine(packages, dir), vpmManifest);
                if (read != null)
                    unlockedPackages.Add(read.Value);
            }

            return new UnityProject(unityFound, vpmManifest, unlockedPackages);
        }

        private static async Task<(string, PackageJson)?> try_read_unlocked_package(string name, string path,
            VpmManifest vpmManifest)
        {
            var packageJsonPath = Path.Combine(path, "package.json");
            var parsed = await load_json_or_else(packageJsonPath, x => new PackageJson(x), () => null);
            if (parsed != null && parsed.name == name && vpmManifest.locked().ContainsKey(name))
                return null;
            return (name, parsed);
        }

        // no find_unity_project_path
    }

    class AddPackageRequest
    {
        (string, VpmDependency)[] _dependencies;
        PackageInfo[] _locked;
        string[] _legacyFiles;
        string[] _legacyFolders;

        public AddPackageRequest((string, VpmDependency)[] dependencies, PackageInfo[] locked, string[] legacy_files, string[] legacy_folders)
        {
            _dependencies = dependencies;
            _locked = locked;
            _legacyFiles = legacy_files;
            _legacyFolders = legacy_folders;
        }
        
        public IReadOnlyList<PackageInfo> locked() => _locked;

        public IReadOnlyList<(string name, VpmDependency dep)> dependencies() => _dependencies;

        public IReadOnlyList<string> legacy_files() => _legacyFiles;

        public IReadOnlyList<string> legacy_folders() => _legacyFolders;
    }

    sealed partial class UnityProject {
        public async Task<AddPackageRequest> add_package_request(
        Environment env,
        List<PackageInfo> packages,
        bool to_dependencies
        ) {
            packages.retain(pkg =>
            {
                var dep = this.manifest.dependencies().get(pkg.name());
                return dep == null || dep.version < pkg.version();
            });

            // if same or newer requested package is in locked dependencies,
            // just add requested version into dependencies
            var dependencies = new List<(string, VpmDependency)>();
            var locked = new List<PackageInfo>();

            foreach (var request in packages)
            {
                var dep = this.manifest.locked().get(request.name());
                var update = dep == null || dep.version < request.version();

                if (to_dependencies) {
                    dependencies.Add((request.name(), new VpmDependency(request.version())));
                }

                if (update) {
                    locked.Add(request);
                }
            }

            if (locked.len() == 0) {
                // early return: 
                return new AddPackageRequest(
                    dependencies: dependencies.ToArray(),
                    locked: Array.Empty<PackageInfo>(),
                    legacy_files: Array.Empty<string>(),
                    legacy_folders: new string[0]
                );
            }

            var resolved = this.collect_adding_packages(env, locked);

            var (legacy_files, legacy_folders) = await this.collect_legacy_assets(resolved);

            return new AddPackageRequest( 
                dependencies: dependencies.ToArray(), 
                locked: resolved,
                legacy_files: legacy_files,
                legacy_folders: legacy_folders
            );
        }

    async Task<(string[], string[])> collect_legacy_assets(IReadOnlyCollection<PackageInfo> packages)  {
        var folders = packages.SelectMany(x => x.package_json().legacy_folders).Select(pair => (pair.Key, pair.Value, false));
        var files = packages.SelectMany(x => x.package_json().legacy_files).Select(pair => (pair.Key, pair.Value, true));
        var assets = new List<(string, string, bool)>(folders.Concat(files));

        const int NotFound = 0;
        const int FoundFile = 2;
        const int FoundFolder = 3;
        const int GuidFile = 4;
        const int GuidFolder = 5;

        bool is_guid(string guid) =>
            guid.Length == 32 &&
            guid.All(x => ('0' <= x && x <= '9') || ('a' <= x && x <= 'f') || ('A' <= x && x <= 'F'));

        var futures = assets.Select(async tuple =>
        {
            var (path, guid, is_file) = tuple;
            // some packages uses '/' as path separator.
            path = path.Replace('\\', '/');
            // for security, deny absolute path.
            if (Path.IsPathRooted(path))
            {
                return (NotFound, null);
            }

            path = Path.Combine(this.project_dir, path);
            var (file_exists, dir_exists) = await Task.Run(() => (File.Exists(path), Directory.Exists(path)));
            if (file_exists && is_file)
                return (FoundFile, path);
            if (dir_exists && !is_file)
                return (FoundFolder, path);

            if (!is_guid(guid))
                return (NotFound, null);
            return is_file ? (GuidFile, guid) : (GuidFolder, guid);
        });

        var found_files = new HashSet<string>();
        var found_folders = new HashSet<string>();
        var find_guids = new Dictionary<string, bool>();

        foreach (var (state, value) in await Task.WhenAll(futures))
        {
            string path;
            switch (state)
            {
                case NotFound:
                    break;
                case FoundFile:
                    path = value.strip_prefix(project_dir);
                    System.Diagnostics.Debug.Assert(path != null, "path != null");
                    found_files.Add(path);
                    break;
                case FoundFolder:
                    path = value.strip_prefix(project_dir);
                    System.Diagnostics.Debug.Assert(path != null, "path != null");
                    found_folders.Add(path);
                    break;
                case GuidFile:
                    find_guids[value] = true;
                    break;
                case GuidFolder:
                    find_guids[value] = false;
                    break;
            }
        }

        if (find_guids.len() != 0) {
            // walk dir
            // VPAI: use AssetDatabase API here.

            foreach (var (guid, is_file) in find_guids)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) continue;

                var is_file_actual = File.Exists(path);
                if (is_file_actual != is_file) continue;

                if (is_file) {
                    found_files.Add(path);
                } else {
                    found_folders.Add(path);
                }
            }
        }

        return (found_files.ToArray(), found_folders.ToArray());
    }

        public async Task do_add_package_request(
            Environment env,
            AddPackageRequest request
        ) {
            // first, add to dependencies
            foreach (var (name, dep) in request.dependencies()) {
                this.manifest.add_dependency(name, dep);
            }

            // then, do install
            await this.do_add_packages_to_locked(env, request.locked());

            // finally try to remove legacy assets
            async Task remove_meta_file(string base_path) {
                try
                {
                    await CsUtils.remove_file($"{base_path}.meta");
                }
                catch (IOException e)
                {
                    Debug.LogError($"removing legacy asset at {base_path}: {e}");
                }
            }

            async Task remove_file(string path) {
                try
                {
                    await CsUtils.remove_file(path);
                }
                catch (IOException e)
                {
                    Debug.LogError($"removing legacy asset at {path}: {e}");
                }
                await remove_meta_file(path);
            }

            async Task remove_folder(string path) {
                try
                {
                    await CsUtils.remove_dir_all(path);
                }
                catch (IOException e)
                {
                    Debug.LogError($"removing legacy asset at {path}: {e}");
                }
                await remove_meta_file(path);
            }

            await Task.WhenAll(request.legacy_files().Select(remove_file)
                .Concat(request.legacy_folders().Select(remove_folder)));
        }

        private async Task do_add_packages_to_locked(Environment env, IReadOnlyList<PackageInfo> packages)
        {
            foreach (var pkg in packages)
                manifest.add_locked(pkg.name(), new VpmLockedDependency(pkg.version(), pkg.vpm_dependencies()));

            var packages_folder = Path.Combine(project_dir, "Packages");

            await Task.WhenAll(packages.Select(x => env.add_package(x, packages_folder)));
        }

        // no remove: VPAI only does adding package
        // no mark_and_sweep


        class DependencyInfo
        {
            public PackageInfo? @using;
            [CanBeNull] public Version current;
            // "" key for root dependencies
            [NotNull] public readonly Dictionary<string, VersionRange> requirements;
            [NotNull] public HashSet<string> dependencies;

            public DependencyInfo()
            {
                @using = null;
                current = null;
                requirements = new Dictionary<string, VersionRange> {};
                dependencies = new HashSet<string>();
            }

            public DependencyInfo(VersionRange range) : this()
            {
                requirements.Add("", range);
            }

            public void add_range(string source, VersionRange range) {
                requirements[source] = range;
            }

            public void remove_range(string source) {
                requirements.Remove(source);
            }

            public void set_using_info(Version version, HashSet<string> dependencies) {
                current = version;
                this.dependencies = dependencies;
            }

            public HashSet<string> set_package(PackageInfo new_pkg) {
                current = new_pkg.version();
                var old = this.dependencies;
                this.dependencies = new HashSet<string>(new_pkg.vpm_dependencies().Keys);
                @using = new_pkg;

                // using is save
                return old;
            }
        }

        PackageInfo[] collect_adding_packages(
        Environment env,
            IEnumerable<PackageInfo> packages
    ) {

            var dependencies = new Dictionary<string, DependencyInfo>();

            // first, add dependencies
            // VPAI: we don't need root_dependencies
            foreach (var (name, dep) in this.manifest.dependencies())
            {
                dependencies[name] = new DependencyInfo(VersionRange.same_or_later(dep.version));
            }

            // VPAI
            DependencyInfo GetOrPut(string pkg)
            {
                if (!dependencies.TryGetValue(pkg, out var dep))
                    dependencies[pkg] = dep = new DependencyInfo();
                return dep;
            }

            // then, add locked dependencies info
            foreach (var (source, locked) in this.manifest.locked())
            {
                GetOrPut(source).set_using_info(locked.version, new HashSet<string>(locked.dependencies.Keys));

                foreach (var (dependency, range) in locked.dependencies)
                    GetOrPut(dependency).add_range(source, range);
            }

            var queue = new LinkedList<PackageInfo>(packages);

            while (queue.Count != 0)
            {
                var x = queue.First.Value;
                queue.RemoveFirst();
                //log::debug!("processing package {} version {}", x.name(), x.version());
                var name = x.name();
                var vpm_dependencies = x.vpm_dependencies();
                var old_dependencies = GetOrPut(name).set_package(x);

                // remove previous dependencies if exists
                foreach (var dep in old_dependencies) {
                    dependencies[dep].remove_range(dep);
                }

                // add new dependencies
                foreach (var (dependency, range) in vpm_dependencies)
                {
                    //log::debug!("processing package {name}: dependency {dependency} version {range}");
                    var entry = GetOrPut(dependency);
                    var install = true;

                    if (queue.Any(y => y.name() == dependency && range.matches(y.version()))) {
                        // if installing version is good, no need to reinstall
                        install = false;
                        //log::debug!("processing package {name}: dependency {dependency} version {range}: pending matches");
                    } else {
                        // if already installed version is good, no need to reinstall
                        
                        if (entry.current is Version version) {
                            if (range.matches(version)) {
                                //log::debug!("processing package {name}: dependency {dependency} version {range}: existing matches");
                                install = false;
                            }
                        }
                    }

                    entry.add_range(name, range);

                    if (install) {
                        var found = env.find_package_by_name(dependency, range.matches);
                        if (found == null)
                            throw new VrcGetException($"dependency not found: {dependency}");

                        // remove existing if existing
                        queue.retain( y => y.name() != dependency);
                        queue.AddLast(found.Value);
                    }
                }
            }

            // finally, check for conflict.
            foreach (var (name, info) in dependencies)
            {
                if (info.current == null) continue;
                foreach (var (source, range) in info.requirements)
                {
                    if (!range.matches(info.current)) {
                        throw new VrcGetException($"Conflict with Dependencies: {name} conflicts with {source}");
                    }
                }
            }

            return dependencies
                .Where(x => x.Value.@using != null)
                .Select(x => x.Value.@using.Value)
                .ToArray();
        }

        public async Task save()
        {
            await manifest.SaveTo(Path.Combine(project_dir, "Packages/vpm-manifest.json"));
        }
        
        // no resolve: VPAI only does installing packages

        // locked_packages

        internal IEnumerable<(string, Dictionary<string, VersionRange>)> all_dependencies()
        {
            var lockedDependencies = manifest.locked().Select(kvp => (kvp.Key, Dependencies: kvp.Value.dependencies));
            var unlockedDependencies = unlocked_packages.Where(x => x.manifest != null)
                .Select(x => (Name: x.manifest.name, VpmDependencies: x.manifest.vpm_dependencies));

            return lockedDependencies.Concat(unlockedDependencies);
        } 
    }

    #region VPAI

    public enum AddPackageStatus
    {
        AlreadyAdded, JustAddToDependency, InstallToLocked
    }

    #endregion
    
    delegate bool VersionSelector(Version version);

    static partial class ModStatics {
        [ItemCanBeNull]
        public static Task<FileStream> try_open_file([NotNull] string path)
        {
            try
            {
                return Task.FromResult(File.OpenRead(path));
            }
            catch (FileNotFoundException)
            {
                return Task.FromResult<FileStream>(null);
            }
        }

        [ItemCanBeNull]
        public static async Task<string> TryReadFile([NotNull] string path)
        {
            try
            {
                return await Task.Run(() => File.ReadAllText(path, Encoding.UTF8));
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
        }

        public static async Task<T> load_json_or_else<T>(string manifestPath, Func<JsonObj, T> parser, Func<T> @default)
        {
            var file = await TryReadFile(manifestPath);
            if (file == null) return @default();
            if (file.StartsWith("\uFEFF", StringComparison.Ordinal))
                file = file.Substring(1);

            return parser(new JsonParser(file).Parse(JsonType.Obj));
        }

        public static async Task<T> load_json_or_default<T>(string manifestPath, Func<JsonObj, T> parser) where T : new()
        {
            return await load_json_or_else(manifestPath, parser, () => new T());
        }
    }
}