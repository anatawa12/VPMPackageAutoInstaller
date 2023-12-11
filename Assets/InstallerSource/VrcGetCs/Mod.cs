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
using static Anatawa12.VrcGet.CsUtils;
using Version = SemanticVersioning.Version;
// ReSharper disable ParameterHidesMember

namespace Anatawa12.VrcGet
{
    sealed partial class Environment
    {
        [CanBeNull] private readonly HttpClient http;
        [NotNull] private readonly Path global_dir;
        [NotNull] private readonly JsonObj settings;
        [NotNull] private readonly RepoHolder repo_cache;
        [NotNull] private readonly List<(Path, PackageJson)> user_packages;
        [NotNull] public readonly List<(Path path, string url)> PendingRepositories = new List<(Path, string)>(); // VPAI
        private bool settings_changed;

        private Environment([CanBeNull] HttpClient http, [NotNull] Path globalDir, [NotNull] JsonObj settings, [NotNull] RepoHolder repoCache, List<(Path, PackageJson)> userPackages, bool settingsChanged)
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
            var folder = new Path(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData));
            folder = folder.join("VRChatCreatorCompanion");

            //Debug.Log($"initializing Environment with config folder {folder}");

            return new Environment
            (
                http: http,
                settings: await load_json_or_default(folder.join("settings.json"), x => x),
                globalDir: folder,
                repoCache: new RepoHolder(),
                userPackages: new List<(Path, PackageJson)>(),
                settingsChanged: false
            );
        }

        public async Task load_package_infos(bool update)
        {
            var http = update ? this.http : null;
            await this.repo_cache.load_repos(http, this.get_repo_sources());
            this.update_user_repo_id();
            await this.load_user_package_infos();
            this.remove_id_duplication();
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
                var id = loaded.id() ?? loaded.url() ?? repo.url;
                if (id != repo.id) {
                    repo.id = id;

                    json[i] = repo.ToJson();
                    this.settings_changed = true;
                }
            }
        }

        void remove_id_duplication() {
            var user_repos = this.get_user_repos();
            if (user_repos.len() == 0)
            {
                return;
            }

            var json = this.settings.Get("userRepos", JsonType.List);

            var used_ids = new HashSet<string>();
            var took = json;
            this.settings.Put("userRepos", json = new List<object>(took.len()), JsonType.List);

            foreach (var (repo, repo_json) in user_repos.Zip(took, (x, y) => (x, y)))
            {
                var to_add = true;
                if (repo.id is string id) {
                    to_add = used_ids.Add(id);
                }
                if (to_add) {
                    // this means new id
                    json.Add(repo_json);
                } else { 
                    // this means duplicated id: removed so mark as changed
                    settings_changed = true;
                    repo_cache.remove_repo(repo.local_path);
                }
            }
        }

        async Task load_user_package_infos()
        {
            var self = this;
            self.user_packages.Clear();
            foreach (var x in self.get_user_package_folders())
            {
                var package_json = await load_json_or_else(x.join("package.json"), y => new PackageJson(y),
                    () => null);
                if (package_json != null)
                {
                    self.user_packages.Add((x, package_json));
                }
            }
        }


        [NotNull]
        public Path get_repos_dir() => global_dir.join("Repos");

        public PackageInfo? find_package_by_name([NotNull] string package, PackageSelector package_selector)
        {
            var versions = find_packages(package);

            versions.RemoveAll(x => !package_selector.satisfies(x));
            versions.Sort((x, y) => y.version().CompareTo(x.version()));

            return versions.Count != 0 ? versions[0] as PackageInfo? : null;
        }

        [ItemNotNull]
        public List<RepoSource> get_repo_sources()
        {
            var definedSources = PreDefinedRepoSource.Sources.Select(x =>
                new PreDefinedRepoSource(x, this.get_repos_dir().joined(x.file_name)));
            var userRepoSources = get_user_repos().Select(x => new UserRepoSource(x));

            return definedSources.Concat<RepoSource>(userRepoSources).ToList();
        }

        [ItemNotNull]
        public LocalCachedRepository[] get_repos() => repo_cache.get_repos();

        [ItemNotNull]
        public IEnumerable<(Path, LocalCachedRepository)> get_repo_with_path() => repo_cache.get_repo_with_path();


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

        public async Task add_package([NotNull] PackageInfo package, [NotNull] Path target_packages_folder)
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
        List<Path> get_user_package_folders() =>
            settings.Get("userPackageFolders", JsonType.List, true)
                ?.Cast<string>()
                .Select(x => new Path(x))
                ?.ToList() ?? new List<Path>();

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
            var localPath = get_repos_dir().joined($"{Guid.NewGuid()}.json");

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
            var localPath = get_repos_dir().join($"{Guid.NewGuid()}.json");
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

        public void add_local_repo([NotNull] Path path, [CanBeNull] string name)
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
        public static PackageInfo local(PackageJson json, Path path) => new PackageInfo(json, path);

        public string name()  => this.package_json().name;
        public Version version()  => this.package_json().version;
        public Dictionary<string, VersionRange> vpm_dependencies()  => this.package_json().vpm_dependencies;
        public string[] legacy_packages()  => this.package_json().legacy_packages;
        [CanBeNull] public PartialUnityVersion unity()  => this.package_json().unity;

        // cs impl
        public bool is_remote() => _info is LocalCachedRepository;
        public LocalCachedRepository remote() => (LocalCachedRepository)_info;
        public Path local() => (Path)_info;
    }

    interface RepoSource
    {
        Task<LocalCachedRepository> VisitLoadRepo([CanBeNull] HttpClient client);
        Path file_path();
    }

    class PreDefinedRepoSource : RepoSource
    {
        public Information Info { get; }
        public Path path { get; }

        public PreDefinedRepoSource(Information info, Path path)
        {
            Info = info;
            this.path = path;
        }

        public Task<LocalCachedRepository> VisitLoadRepo(HttpClient client) => RepoHolder.LoadPreDefinedRepo(client, this);

        public Path file_path() => path;

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

        public Path file_path() => Setting.local_path;
    }

    static partial class ModStatics
    {
        public static async Task update_from_remote([CanBeNull] HttpClient client, [NotNull] Path path,
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

        public static async Task write_repo([NotNull] Path path, [NotNull] LocalCachedRepository repo)
        {
            var dir = path.parent();
            Debug.Assert(dir != null, nameof(dir) + " != null");
            await create_dir_all(dir);
            await WriteAllText(path, JsonWriter.Write(repo.ToJson()));
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
            // ReadAsStringAsync skips BOM automatically
            var repo = new Repository(new JsonParser(content).Parse(JsonType.Obj));
            repo.set_url_if_none(url);
            return (repo, newEtag);
        }
    }

    partial class Environment {
        public async Task<bool> remove_repo(Func<UserRepoSetting, bool> condition)
        {
            var removes = get_user_repos()
                .Select((x, i) => (x, i))
                .Where(x => condition(x.x))
                .ToList();
            removes.Reverse();
            if (removes.Count == 0) return false;

            var userRepos = settings.Get("userRepos", JsonType.List);
            for (var i = 0; i < removes.Count; i++)
                userRepos.RemoveAt(removes[i].i);

            foreach (var (x, _) in removes)
                await remove_file(x.local_path);
            return true;
        }

        public async Task save()
        {
            if (!settings_changed) return;

            await create_dir_all(global_dir);
            await WriteAllText(global_dir.join("settings.json"), JsonWriter.Write(settings));
            settings_changed = false;
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

        public void remove_packages(IEnumerable<string> names) {
            foreach (var name in names) {
                _locked.Remove(name);
                if (_body.Get("locked", JsonType.Obj) is JsonObj locked)
                    locked.Obj.RemoveAll(x => x.Item1 == name);
                _dependencies.Remove(name);
                if (_body.Get("dependencies", JsonType.Obj) is JsonObj dependencies)
                    dependencies.Obj.RemoveAll(x => x.Item1 == name);
            }
            _changed = true;
        }

        public async Task SaveTo(Path file)
        {
            if (!_changed) return;
            await WriteAllText(file, JsonWriter.Write(_body));
            _changed = false;
        }

        // no mark_and_sweep_packages
    }

    #endregion

    sealed partial class UnityProject
    {
        private readonly Path project_dir;
        public readonly VpmManifest manifest; // VPAI: public
        [CanBeNull] public UnityVersion unity_version; // VPAI: public
        private readonly List<(string dirName, PackageJson manifest)> unlocked_packages;
        private readonly Dictionary<string, PackageJson> installed_packages;

        private UnityProject(Path project_dir, VpmManifest manifest, List<(string, PackageJson)> unlockedPackages, Dictionary<string, PackageJson> installed_packages, [CanBeNull] UnityVersion unityVersion)
        {
            this.project_dir = project_dir;
            this.manifest = manifest;
            this.unlocked_packages = unlockedPackages;
            this.installed_packages = installed_packages;
            unity_version = unityVersion;
        }

        public static async Task<UnityProject> find_unity_project([NotNull] Path unityProject)
        {
            // removed find support
            var unityFound = unityProject; //?? findUnityProjectPath();

            var manifest = unityFound.join("Packages").joined("vpm-manifest.json");
            var vpmManifest = new VpmManifest(await load_json_or_default(manifest, x => x));

            var installed_packages = new Dictionary<string, PackageJson>();
            var unlockedPackages = new List<(string, PackageJson)>();

            var packages = unityFound.join("Packages");
            foreach (var dir in await Task.Run(() => Directory.GetDirectories(packages.AsString)))
            {
                var read = await try_read_unlocked_package(dir, packages.join(dir));
                var is_installed = false;
                if (read.Item2 is PackageJson parsed)
                {
                    if (parsed.name == read.Item1 && vpmManifest.locked().ContainsKey(parsed.name))
                        is_installed = true;
                }

                if (is_installed)
                    installed_packages[read.Item1] = read.Item2;
                else
                    unlockedPackages.Add(read);
            }

            var unity_version = try_read_unity_version(unityFound);

            return new UnityProject(unityFound, vpmManifest, unlockedPackages, installed_packages, unity_version);
        }

        private static async Task<(string, PackageJson)> try_read_unlocked_package(string name, Path path)
        {
            var packageJsonPath = path.join("package.json");
            var parsed = await load_json_or_else(packageJsonPath, x => new PackageJson(x), () => null);
            return (name, parsed);
        }

        // no find_unity_project_path

        private static UnityVersion try_read_unity_version(Path _path)
        {
            // VPAI: use unity api
            return UnityVersion.parse(Application.unityVersion);
        }
    }

    class AddPackageRequest
    {
        (string, VpmDependency)[] _dependencies;
        PackageInfo[] _locked;
        Path[] _legacyFiles;
        Path[] _legacyFolders;
        string[] _legacy_packages;
        IReadOnlyDictionary<string, List<string>> _conflicts;
        private string[] _unity_conflicts;

        public AddPackageRequest((string, VpmDependency)[] dependencies, PackageInfo[] locked, Path[] legacy_files, Path[] legacy_folders, string[] legacy_packages, Dictionary<string, List<string>> conflicts, string[] unity_conflicts)
        {
            _dependencies = dependencies;
            _locked = locked;
            _legacyFiles = legacy_files;
            _legacyFolders = legacy_folders;
            _legacy_packages = legacy_packages;
            _conflicts = conflicts;
            _unity_conflicts = unity_conflicts;
        }
        
        public IReadOnlyList<PackageInfo> locked() => _locked;

        public IReadOnlyList<(string name, VpmDependency dep)> dependencies() => _dependencies;

        public IReadOnlyList<Path> legacy_files() => _legacyFiles;

        public IReadOnlyList<Path> legacy_folders() => _legacyFolders;

        public IReadOnlyList<string> legacy_packages() => _legacy_packages;

        public IReadOnlyDictionary<string, List<string>> conflicts() => _conflicts;
        public string[] unity_conflicts() => _unity_conflicts;
    }

    sealed partial class UnityProject {
        public async Task<AddPackageRequest> add_package_request(
        Environment env,
        List<PackageInfo> packages,
        bool to_dependencies,
        bool allow_prerelease
        ) {
            packages.retain(pkg =>
            {
                var dep = this.manifest.dependencies().get(pkg.name());
                return dep == null || dep.version.matches(pkg.version());
            });

            // if same or newer requested package is in locked dependencies,
            // just add requested version into dependencies
            var dependencies = new List<(string, VpmDependency)>();
            var adding_packages = new List<PackageInfo>();

            foreach (var request in packages)
            {
                var dep = this.manifest.locked().get(request.name());
                var update = dep == null || dep.version < request.version();

                if (to_dependencies) {
                    dependencies.Add((request.name(), new VpmDependency(request.version())));
                }

                if (update) {
                    adding_packages.Add(request);
                }
            }

            if (adding_packages.len() == 0) {
                // early return: 
                return new AddPackageRequest(
                    dependencies: dependencies.ToArray(),
                    locked: Array.Empty<PackageInfo>(),
                    legacy_files: Array.Empty<Path>(),
                    legacy_folders: Array.Empty<Path>(),
                    legacy_packages: Array.Empty<string>(),
                    conflicts: new Dictionary<string, List<string>>(0),
                    unity_conflicts: Array.Empty<string>()
                );
            }

            var result = package_resolution.collect_adding_packages(manifest.dependencies(), manifest.locked(), unity_version, env, adding_packages, allow_prerelease);

            var legacy_packages = result.found_legacy_packages.Where(name => manifest.locked().ContainsKey(name)).ToArray();

            var (legacy_files, legacy_folders) = await this.collect_legacy_assets(result.new_packages);

            /*
             *
             // rewrite following rust in C#
        let unity_conflicts = if let Some(unity) = self.unity_version {
            result.new_packages
                .iter()
                .filter(|pkg| !unity_compatible(pkg, unity))
                .map(|pkg| pkg.name().to_owned())
                .collect()
        } else {
            vec![]
        };
             */

            var unity_conflicts = unity_version != null
                ? result.new_packages
                    .Where(pkg => !unity_compatible(pkg, unity_version))
                    .Select(pkg => pkg.name())
                    .ToArray()
                : Array.Empty<string>();

            return new AddPackageRequest( 
                dependencies: dependencies.ToArray(), 
                locked: result.new_packages,
                conflicts: result.conflicts,
                legacy_files: legacy_files,
                legacy_folders: legacy_folders,
                legacy_packages: legacy_packages,
                unity_conflicts: unity_conflicts
            );
        }
    }

    readonly struct LegacyInfo
    {
        public const int KindNotFound = 0;
        public const int KindFoundFile = 2;
        public const int KindFoundFolder = 3;
        public const int KindGuidFile = 4;
        public const int KindGuidFolder = 5;
        private readonly int _kind;
        private readonly object _value;

        public int Kind => _kind;
        public string GUID => (string)_value;
        public Path Path => (Path)_value;

        public static LegacyInfo NotFound = new LegacyInfo(KindNotFound, null);
        public static LegacyInfo FoundFile(Path path) => new LegacyInfo(KindFoundFile, path);
        public static LegacyInfo FoundFolder(Path path) => new LegacyInfo(KindFoundFolder, path);
        public static LegacyInfo GuidFile(string guid) => new LegacyInfo(KindGuidFile, guid);
        public static LegacyInfo GuidFolder(string guid) => new LegacyInfo(KindGuidFolder, guid);

        private LegacyInfo(int kind, object value)
        {
            _kind = kind;
            _value = value;
        }
    }

    sealed partial class UnityProject {

    async Task<(Path[], Path[])> collect_legacy_assets(IReadOnlyCollection<PackageInfo> packages)  {
        var folders = packages.SelectMany(x => x.package_json().legacy_folders).Select(pair => (pair.Key, pair.Value, false));
        var files = packages.SelectMany(x => x.package_json().legacy_files).Select(pair => (pair.Key, pair.Value, true));
        var assets = new List<(string, string, bool)>(folders.Concat(files));

        bool is_guid(string guid) =>
            guid != null && 
            guid.Length == 32 &&
            guid.All(x => ('0' <= x && x <= '9') || ('a' <= x && x <= 'f') || ('A' <= x && x <= 'F'));

        var futures = assets.Select(async tuple =>
        {
            var (_, guid, is_file) = tuple;
            // some packages uses '/' as path separator.
            var path = new Path(tuple.Item1.Replace('\\', '/'));
            // for security, deny absolute path.
            if (path.has_root())
            {
                return LegacyInfo.NotFound;
            }

            path = this.project_dir.join(path);
            var (file_exists, dir_exists) = await Task.Run(() => (File.Exists(path.AsString), Directory.Exists(path.AsString))); // VPAI: C# impl
            if (file_exists && is_file)
                return LegacyInfo.FoundFile(path);
            if (dir_exists && !is_file)
                return LegacyInfo.FoundFolder(path);

            if (!is_guid(guid))
                return LegacyInfo.NotFound;
            return is_file ? LegacyInfo.GuidFile(guid) : LegacyInfo.GuidFolder(guid);
        });

        var found_files = new HashSet<Path>();
        var found_folders = new HashSet<Path>();
        var find_guids = new Dictionary<string, bool>();

        foreach (var info in await Task.WhenAll(futures))
        {
            Path path;
            switch (info.Kind)
            {
                case LegacyInfo.KindNotFound:
                    break;
                case LegacyInfo.KindFoundFile:
                    path = info.Path.strip_prefix(project_dir);
                    System.Diagnostics.Debug.Assert(path != null, "path != null");
                    found_files.Add(path);
                    break;
                case LegacyInfo.KindFoundFolder:
                    path = info.Path.strip_prefix(project_dir);
                    System.Diagnostics.Debug.Assert(path != null, "path != null");
                    found_folders.Add(path);
                    break;
                case LegacyInfo.KindGuidFile:
                    find_guids[info.GUID] = true;
                    break;
                case LegacyInfo.KindGuidFolder:
                    find_guids[info.GUID] = false;
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
                    found_files.Add(new Path(path));
                } else {
                    found_folders.Add(new Path(path));
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

            // then, do install packages
            await this.do_add_packages_to_locked(env, request.locked());

            //VPAI: skip project_dir since this runs under unity project
            //var project_dir = this.project_dir;

            // finally, try to remove legacy assets
            manifest.remove_packages(request.legacy_packages());
            await Task.WhenAll(
                request.legacy_files().Select(remove_file)
                    .Concat(request.legacy_folders().Select(remove_folder))
                    .Concat(request.legacy_packages().Select(remove_package))
            );

            async Task remove_meta_file(Path base_path) {
                var meta = new Path(base_path.AsString + ".meta");
                try
                {
                    await CsUtils.remove_file(meta);
                }
                catch (IOException e)
                {
                    Debug.LogError($"removing legacy asset at {base_path}: {e}");
                }
            }

            async Task remove_file(Path path) {
                path = project_dir.join(path);
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

            async Task remove_folder(Path path) {
                path = project_dir.join(path);
                try
                {
                    await remove_dir_all(path);
                }
                catch (IOException e)
                {
                    Debug.LogError($"removing legacy asset at {path}: {e}");
                }
                await remove_meta_file(path);
            }
            
            async Task remove_package(string name) {
                var folder = project_dir.join("Packages").joined(name);
                try
                {
                    await remove_dir_all(folder);
                }
                catch (IOException e)
                {
                    Debug.LogError($"removing legacy package at {name}: {e}");
                }
            }
        }

        private async Task do_add_packages_to_locked(Environment env, IReadOnlyList<PackageInfo> packages)
        {
            foreach (var pkg in packages)
                manifest.add_locked(pkg.name(), new VpmLockedDependency(pkg.version(), pkg.vpm_dependencies()));

            var packages_folder = project_dir.join("Packages");

            await Task.WhenAll(packages.Select(x => env.add_package(x, packages_folder)));
        }

        // no remove: VPAI only does adding package
        // no mark_and_sweep

        public async Task save()
        {
            await manifest.SaveTo(project_dir.join("Packages").joined("vpm-manifest.json"));
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

        // unlocked_packages
        // get_installed_package
    }

    #region VPAI

    public enum AddPackageStatus
    {
        AlreadyAdded, JustAddToDependency, InstallToLocked
    }

    #endregion

    partial class PackageSelector
    {
        [CanBeNull] UnityVersion project_unity;
        [NotNull] VersionSelector version_selector;

        private PackageSelector([CanBeNull] UnityVersion projectUnity, [NotNull] VersionSelector versionSelector)
        {
            project_unity = projectUnity;
            version_selector = versionSelector;
        }

        public static PackageSelector specific_version(Version version) =>
            new PackageSelector(null, version.Equals);

        public static PackageSelector latest_for(UnityVersion unityVersion, bool include_prerelease) =>
            new PackageSelector(unityVersion,
                include_prerelease ? (VersionSelector)(_ => true) : version => !version.IsPreRelease);

        public static PackageSelector range_for([CanBeNull] UnityVersion unityVersion, VersionRange range) =>
            new PackageSelector(unityVersion, range.matches);

        public static PackageSelector ranges_for(UnityVersion unityVersion, VersionRange[] ranges) =>
            new PackageSelector(unityVersion, version => ranges.All(range => range.matches(version)));

        // VPAI
        public static PackageSelector range_for_pre([CanBeNull] UnityVersion unityVersion, VersionRange range, bool prerelease) =>
            new PackageSelector(unityVersion, version => range.match_pre(version, prerelease));
    }

    static partial class ModStatics
    {
        public static bool unity_compatible(PackageInfo package, UnityVersion unity)
        {
            bool is_vrcsdk_for_2019(Version version) => version.Major == 3 && version.Minor <= 4;
            bool is_resolver_for_2019(Version version) => version.Major == 0 && version.Minor == 1 && version.Patch <= 26;

            switch (package.name())
            {
                case "com.vrchat.avatars":
                case "com.vrchat.worlds":
                case "com.vrchat.base" when is_vrcsdk_for_2019(package.version()):
                    return unity.major() == 2019;
                case "com.vrchat.core.vpm-resolver" when is_resolver_for_2019(package.version()):
                    return unity.major() == 2019;
                default:
                    if (package.unity() is PartialUnityVersion min_unity)
                        return unity >= new UnityVersion(min_unity.major, min_unity.minor, 0, ReleaseType.Alpha, 0);
                    return true;
            }
        }
    }

    partial class PackageSelector
    {
        public bool satisfies(PackageInfo package)
        {
            if (project_unity != null && !unity_compatible(package, project_unity))
                return false;
            return version_selector(package.version());
        }
    }

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
        public static async Task<string> TryReadFile([NotNull] Path path)
        {
            try
            {
                return await Task.Run(() => File.ReadAllText(path.ToString(), Encoding.UTF8));
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

        public static async Task<T> load_json_or_else<T>(Path manifestPath, Func<JsonObj, T> parser, Func<T> @default)
        {
            var file = await TryReadFile(manifestPath);
            if (file == null) return @default();
            if (file.StartsWith("\uFEFF", StringComparison.Ordinal))
                file = file.Substring(1);

            return parser(new JsonParser(file).Parse(JsonType.Obj));
        }

        public static async Task<T> load_json_or_default<T>(Path manifestPath, Func<JsonObj, T> parser) where T : new()
        {
            return await load_json_or_else(manifestPath, parser, () => new T());
        }
    }
}
