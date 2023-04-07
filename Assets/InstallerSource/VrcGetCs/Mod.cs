// ReSharper disable InconsistentNaming
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Anatawa12.SimpleJson;
using JetBrains.Annotations;
using static Anatawa12.VrcGet.ModStatics;
using Debug = UnityEngine.Debug;
using Version = SemanticVersioning.Version;

namespace Anatawa12.VrcGet
{
    class Environment
    {
        [CanBeNull] private readonly HttpClient http;
        [NotNull] private readonly string global_dir;
        [NotNull] private readonly JsonObj settings;
        [NotNull] private readonly RepoHolder repo_cache;
        [NotNull] public readonly List<LocalCachedRepository> PendingRepositories = new List<LocalCachedRepository>(); // VPAI
        private bool settings_changed;

        private Environment([CanBeNull] HttpClient http, [NotNull] string globalDir, [NotNull] JsonObj settings, [NotNull] RepoHolder repoCache, bool settingsChanged)
        {
            this.http = http;
            global_dir = globalDir;
            this.settings = settings;
            repo_cache = repoCache;
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
                settingsChanged: false
            );
        }

        [NotNull]
        public string get_repos_dir() => Path.Combine(global_dir, "Repos");

        [ItemCanBeNull]
        public async Task<PackageJson> find_package_by_name([NotNull] string package, VersionSelector version)
        {
            var versions = await find_packages(package);

            versions.RemoveAll(x => !version(x.version));
            versions.Sort((x, y) => y.version.CompareTo(x.version));

            return versions.Count != 0 ? versions[0] : null;
        }

        [ItemNotNull]
        public async Task<List<IRepoSource>> get_repo_sources()
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

            var definedSources = PreDefinedRepoSource.Sources;
            var userRepoSources = userRepos.Select(x => new UserRepoSource(x));
            var pendingRepoSources = PendingRepositories.Select(x => new PendingSource(x)); // VPAI

            return await Task.Run(() =>
                undefinedRepos.Concat<IRepoSource>(definedSources).Concat(userRepoSources).Concat(pendingRepoSources).ToList());
        }

        [ItemNotNull]
        public async Task<List<LocalCachedRepository>> get_repos()
        {
            return (await Task.WhenAll((await get_repo_sources()).Select(get_repo))).ToList();
        }

        [ItemNotNull]
        public Task<LocalCachedRepository> get_repo(IRepoSource source)
        {
            return source.GetRepo(this, repo_cache);
        }


        [ItemNotNull]
        public async Task<List<PackageJson>> find_packages([NotNull] string package)
        {
            var list = new List<PackageJson>();

            (await get_repos())
                .Select(repo => repo.cache.Get(package, JsonType.Obj, true))
                .Where(x => x != null)
                .Select(json => new PackageVersions(json))
                .SelectMany(x => x.versions.Values)
                .AddAllTo(list);

            // user package folders
            foreach (var x in get_user_package_folders())
            {
                var packageJson =
                    await load_json_or_else(Path.Combine(x, "package.json"), y => new PackageJson(y), () => null);
                if (packageJson != null && packageJson.name == package) {
                    list.Add(packageJson);
                }
            }

            return list;
        }
        
        [ItemNotNull]
        public async Task<List<PackageJson>> find_whole_all_packages([NotNull] Func<PackageJson, bool> filter)
        {
            var list = new List<PackageJson>();

            //[CanBeNull] // C# 9.0
            PackageJson GetLatest(PackageVersions versions) =>
                versions
                    .versions
                    .Values
                    .Where(x => !x.version.IsPreRelease)
                    .MaxBy(x => x.version);

            (await get_repos())
                .SelectMany(x => x.cache.Select(y => y.Item2))
                .Select(x => new PackageVersions((JsonObj)x))
                .Select(GetLatest)
                .Where(x => x != null)
                .Where(filter)
                .AddAllTo(list);

            // user package folders
            foreach (var x in get_user_package_folders())
            {
                var packageJson = await load_json_or_else(Path.Combine(x, "package.json"), y => new PackageJson(y), () => null);
                if (packageJson != null && filter(packageJson))
                {
                    list.Add(packageJson);
                }
            }

            list.Sort((x, y) => y.version.CompareTo(x.version));

            return list.DistinctBy(x => (Name: x.name, Version: x.version)).ToList();
        }

        public async Task add_package([NotNull] PackageJson package, [NotNull] string targetPackagesFolder)
        {
            var zipFileName = $"vrc-get-{package.name}-{package.version}.zip";
            var zipPath = Path.Combine(get_repos_dir(), package.name, zipFileName);
            Directory.CreateDirectory(Path.Combine(get_repos_dir(), package.name));
            var shaPath = Path.Combine(get_repos_dir(), package.name, $"{zipFileName}.sha256");
            var destDir = Path.Combine(targetPackagesFolder, package.name);

            //[CanBeNull]
            byte[] ParseHex(/*[NotNull]*/ byte[] hex)
            {
                byte ParseChar(byte c)
                {
                    if ('0' <= c && c <= '9') return (byte)(c - '0');
                    if ('a' <= c && c <= 'f') return (byte)(c - 'a' + 10);
                    if ('A' <= c && c <= 'F') return (byte)(c - 'A' + 10);
                    return 255;
                }
                var result = new byte[hex.Length / 2];
                for (var i = 0; i < result.Length; i++)
                {
                    var upper = ParseChar(hex[i * 2 + 0]);
                    var lower = ParseChar(hex[i * 2 + 0]);
                    if (upper == 255 || lower == 255) return null;
                    result[i] = (byte)(upper << 4 | lower);
                }
                return result;
            }

            //[NotNull]
            string ToHex(/*[NotNull] */byte[] data) {
                var result = new char[data.Length * 2];
                for (var i = 0; i < data.Length; i++)
                {
                    result[i * 2 + 0] = "0123456789abcdef"[(data[i] >> 4) & 0xf];
                    result[i * 2 + 1] = "0123456789abcdef"[(data[i] >> 0) & 0xf];
                }

                return new string(result);
            }

            async Task<FileStream> TryCache(/*string zipPath, string shaPath*/)
            {
                FileStream result = null;
                FileStream cacheFile = null;
                try
                {
                    cacheFile = File.OpenRead(zipPath);
                    using (var shaFile = File.OpenRead(shaPath))
                    {
                        var shaBuffer = new byte[256 / 8];
                        await shaFile.ReadExactAsync(shaBuffer);
                        var hex = ParseHex(shaBuffer);

                        byte[] hash;

                        using (var sha256 = SHA256.Create()) hash = sha256.ComputeHash(cacheFile);

                        if (!hash.SequenceEqual(hex)) return null;

                        cacheFile.Seek(0, SeekOrigin.Begin);
                        return result = cacheFile;
                    }
                }
                catch
                {
                    return null;
                }
                finally
                {
                    if (cacheFile != null && cacheFile != result)
                        cacheFile.Dispose();
                }
            }

            var zipFile = await TryCache();
            try
            {
                if (zipFile == null)
                {
                    if (http == null)
                        throw new IOException("Offline mode");

                    zipFile = File.Open(zipPath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                    zipFile.Position = 0;

                    var response = await http.GetAsync(package.url, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();

                    var responseStream = await response.Content.ReadAsStreamAsync();

                    await responseStream.CopyToAsync(zipFile);

                    await zipFile.FlushAsync();
                    zipFile.Position = 0;

                    byte[] hash;
                    using (var sha256 = SHA256.Create()) hash = sha256.ComputeHash(zipFile);
                    zipFile.Position = 0;

                    // write SHA file
                    File.WriteAllText(shaPath, $"{ToHex(hash)} {zipFileName}\n");
                }

                try
                {
                    Directory.Delete(destDir);
                }
                catch
                {
                    //ignored
                }

                using (var archive = new ZipArchive(zipFile, ZipArchiveMode.Read, false))
                    archive.ExtractToDirectory(destDir);
            }
            finally
            {
                zipFile?.Dispose();
            }
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

        public async Task add_remote_repo([NotNull] string url, [CanBeNull] string name)
        {
            if (get_user_repos().Any(x => x.url == url))
                throw new VrcGetException("Already Added");
            if (http == null)
                throw new OfflineModeException();


            var response = await download_remote_repository(http, url, null);
            Debug.Assert(response != null, nameof(response) + " != null");
            var (remoteRepo, etag) = response.Value;
            var localPath = Path.Combine(get_repos_dir(), $"{Guid.NewGuid()}.json");

            if (name == null)
                name = remoteRepo.Get("name", JsonType.String, true);

            
            var localCache = new LocalCachedRepository(localPath, name, url);
            localCache.cache = remoteRepo.Get("packages", JsonType.Obj, true) ?? new JsonObj();
            localCache.repo = remoteRepo;
            // set etag
            if (etag != null) {
                if (localCache.vrc_get == null)
                    localCache.vrc_get = new VrcGetMeta();
                localCache.vrc_get.etag = etag;
            }
            await write_repo(localPath, localCache);

            add_user_repo(new UserRepoSetting(localPath, name, url));
        }

        #region VPAI

        public async Task AddPendingRepository([NotNull] string url, [CanBeNull] string name)
        {
            if (get_user_repos().Any(x => x.url == url))
                return; // allow already added
            if (http == null)
                throw new OfflineModeException();

            var response = await download_remote_repository(http, url, null);
            Debug.Assert(response != null, nameof(response) + " != null");
            var (remoteRepo, etag) = response.Value;
            var localPath = $"com.anatawa12.vpai.virtually-added.{Guid.NewGuid()}.json";

            if (name == null)
                name = remoteRepo.Get("name", JsonType.String, true);

            var localCache = new LocalCachedRepository(localPath, name, url);
            localCache.cache = remoteRepo.Get("packages", JsonType.Obj, true) ?? new JsonObj();
            localCache.repo = remoteRepo;
            // set etag
            if (etag != null) {
                if (localCache.vrc_get == null)
                    localCache.vrc_get = new VrcGetMeta();
                localCache.vrc_get.etag = etag;
            }
            PendingRepositories.Add(localCache);
        }

        public async Task SavePendingRepositories()
        {
            for (var i = PendingRepositories.Count - 1; i >= 0; i--)
            {
                var localCache = PendingRepositories[i];
                PendingRepositories.RemoveAt(i);

                var localPath = Path.Combine(get_repos_dir(), $"{Guid.NewGuid()}.json");
                await write_repo(localPath, localCache);
                Debug.Assert(localCache.creation_info != null, "localCache.CreationInfo != null");
                localCache.creation_info.local_path = localPath;
                var name = localCache.creation_info.name;
                var url = localCache.creation_info.url;
                add_user_repo(new UserRepoSetting(localPath, name, url));                
            }
        }

        #endregion

        public Task add_local_repo([NotNull] string path, [CanBeNull] string name)
        {
            if (get_user_repos().Any(x => x.local_path == path))
                throw new VrcGetException("Already Added");

            add_user_repo(new UserRepoSetting(path, name, null));

            return Task.CompletedTask;
        }

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
        private readonly Dictionary<string, VpmDependency> dependencies;
        private readonly Dictionary<string, VpmLockedDependency> locked;
        private bool _changed;

        public IReadOnlyDictionary<string, VpmDependency> Dependencies => dependencies;
        public IReadOnlyDictionary<string, VpmLockedDependency> Locked => locked;

        public VpmManifest(JsonObj body)
        {
            _body = body;

            dependencies = body.Get("dependencies", JsonType.Obj, true)
                               ?.ToDictionary(x => x.Item1, x => new VpmDependency((JsonObj)x.Item2))
                           ?? new Dictionary<string, VpmDependency>();
            locked = body.Get("locked", JsonType.Obj, true)
                         ?.ToDictionary(x => x.Item1, x => new VpmLockedDependency((JsonObj)x.Item2))
                     ?? new Dictionary<string, VpmLockedDependency>();
        }

        public void add_dependency(string name, VpmDependency dependency)
        {
            dependencies[name] = dependency;
            add_value("dependencies", name, dependency.ToJson());
        }

        public void add_locked(string name, VpmLockedDependency dependency)
        {
            locked[name] = dependency;
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

    class UnityProject
    {
        private readonly string _packagesDir;
        // ReSharper disable once InconsistentNaming
        public readonly VpmManifest _manifest; // VPAI: public
        private readonly List<(string dirName, PackageJson manifest)> _unlockedPackages;

        private UnityProject(string packagesDir, VpmManifest manifest, List<(string, PackageJson)> unlockedPackages)
        {
            this._packagesDir = packagesDir;
            this._manifest = manifest;
            this._unlockedPackages = unlockedPackages;
        }

        public static async Task<UnityProject> find_unity_project([NotNull] string unityProject)
        {
            // removed find support
            var unityFound = unityProject; //?? findUnityProjectPath();

            unityFound = Path.Combine(unityFound, "Packages");
            var manifest = Path.Combine(unityFound, "vpm-manifest.json");
            var vpmManifest = new VpmManifest(await load_json_or_default(manifest, x => x));

            var unlockedPackages = new List<(string, PackageJson)>();

            foreach (var dir in await Task.Run(() => Directory.GetDirectories(unityFound)))
            {
                var read = await try_read_unlocked_package(dir, Path.Combine(unityFound, dir), vpmManifest);
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
            if (parsed != null && parsed.name == name && vpmManifest.Locked.ContainsKey(name))
                return null;
            return (name, parsed);
        }

        // no find_unity_project_path

        #region VPAI

        public AddPackageStatus CheckAddPackage(PackageJson request)
        {
            if (_manifest.Dependencies.TryGetValue(request.name, out var dependency) &&
                dependency.version >= request.version)
                return AddPackageStatus.AlreadyAdded;
            if (_manifest.Locked.TryGetValue(request.name, out var locked) && 
                locked.version >= request.version)
                return AddPackageStatus.JustAddToDependency;
            return AddPackageStatus.InstallToLocked;
        }

        #endregion

        public async Task add_package(Environment env, PackageJson request)
        {
            if (_manifest.Dependencies.TryGetValue(request.name, out var dependency) &&
                dependency.version >= request.version)
                throw new VrcGetException("AlreadyNewerPackageInstalled");
            if (_manifest.Locked.TryGetValue(request.name, out var locked) && 
                locked.version >= request.version)
            {
                _manifest.add_dependency(request.name, new VpmDependency(request.version));
                return;
            }

            List<PackageJson> packages = await collect_adding_packages(env, request);
            packages.Add(request);

            check_adding_package(packages);

            _manifest.add_dependency(request.name, new VpmDependency(request.version));

            await do_add_packages_to_locked(env, packages);
        }

        // VPAI: make public
        public void check_adding_package(IEnumerable<PackageJson> packages)
        {
            foreach (var package in packages)
                check_conflict(package.name, package.version);
        }

        // VPAI: make public
        public async Task do_add_packages_to_locked(Environment env, List<PackageJson> packages)
        {
            foreach (var pkg in packages)
                _manifest.add_locked(pkg.name, new VpmLockedDependency(pkg.version, pkg.vpm_dependencies));

            await Task.WhenAll(packages.Select(x => env.add_package(x, _packagesDir)));
        }

        // no upgrade_package: VPAI only does adding package.
        // no remove: VPAI only does adding package
        // no mark_and_sweep

        // VPAI: make public
        public async Task<List<PackageJson>> collect_adding_packages(Environment env, params PackageJson[] packages)
        {
            var allDeps = new List<PackageJson>();
            foreach (var packageJson in packages)
                await collect_adding_packages_internal(allDeps, env, packageJson);
            // ReSharper disable once ForCanBeConvertedToForeach
            // size of all_deps will increase in the loop
            for (var i = 0; i < allDeps.Count; i++)
                await collect_adding_packages_internal(allDeps, env, allDeps[i]);
            return allDeps;
        }

        private async Task collect_adding_packages_internal([ItemNotNull] List<PackageJson> addingDeps, Environment env, PackageJson pkg)
        {
            foreach (var (dep, range) in pkg.vpm_dependencies)
            {
                var installed = _manifest.Locked.TryGetValue(dep, out var locked) ? locked.version : null;
                if (installed == null || !range.IsSatisfied(installed))
                {
                    var found = await env.find_package_by_name(dep, v => range.IsSatisfied(v));
                    if (found == null)
                        throw new VrcGetException($"Dependency ({dep}) Not Found");
                    addingDeps.Add(found);
                }
            }
        }

        private void check_conflict(string name, Version version)
        {
            foreach (var (pkgName, dependencies) in all_dependencies())
            {
                if (dependencies.TryGetValue(name, out var dep))
                {
                    if (!dep.IsSatisfied(version))
                    {
                        throw new VrcGetException($"Conflict with Dependencies: {name} conflicts with {pkgName}");
                    }
                }
            }

            foreach (var (dirName, packageJson) in _unlockedPackages)
            {
                if (dirName == name)
                {
                    throw new VrcGetException($"Conflicts with unlocked package: {name}");
                }

                if (packageJson == null) continue;

                if (packageJson.name == name)
                {
                    throw new VrcGetException($"Conflicts with unlocked package: {name}");
                }
            }
        }

        public async Task save()
        {
            await _manifest.SaveTo(Path.Combine(_packagesDir, "vpm-manifest.json"));
        }
        
        // no resolve: VPAI only does installing packages

        // locked_packages

        internal IEnumerable<(string, Dictionary<string, VersionRange>)> all_dependencies()
        {
            var lockedDependencies = _manifest.Locked.Select(kvp => (kvp.Key, Dependencies: kvp.Value.dependencies));
            var unlockedDependencies = _unlockedPackages.Where(x => x.manifest != null)
                .Select(x => (Name: x.manifest.name, VpmDependencies: x.manifest.vpm_dependencies));

            return lockedDependencies.Concat(unlockedDependencies);
        } 
    }

    #region repoSources

    interface IRepoSource
    {
        Task<LocalCachedRepository> GetRepo(Environment environment, RepoHolder repoCache);
    }

    class PreDefinedRepoSource : IRepoSource
    {
        public string FileName { get; }
        public string URL { get; }
        public string Name { get; }

        private PreDefinedRepoSource(string fileName, string url, string name)
        {
            FileName = fileName;
            URL = url;
            Name = name;
        }

        public static readonly PreDefinedRepoSource Official = new PreDefinedRepoSource("vrc-official.json",
            "https://packages.vrchat.com/official?download", "Official");
        public static readonly PreDefinedRepoSource Curated = new PreDefinedRepoSource("vrc-curated.json",
            "https://packages.vrchat.com/curated?download", "Curated");

        public static readonly PreDefinedRepoSource[] Sources = { Official, Curated };

        public async Task<LocalCachedRepository> GetRepo(Environment environment, RepoHolder repoCache)
        {
            return await repoCache.get_or_create_repo(Path.Combine(environment.get_repos_dir(), FileName), URL, Name);
        }
    }

    class UserRepoSource : IRepoSource
    {
        public UserRepoSetting Setting;

        public UserRepoSource(UserRepoSetting setting)
        {
            Setting = setting;
        }

        public async Task<LocalCachedRepository> GetRepo(Environment environment, RepoHolder repoCache)
        {
            return await repoCache.get_user_repo(Setting);
        }
    }

    class UndefinedSource : IRepoSource
    {
        public string Path;

        public UndefinedSource(string path)
        {
            Path = path;
        }

        public async Task<LocalCachedRepository> GetRepo(Environment environment, RepoHolder repoCache)
        {
            return await repoCache.get_repo(Path, () => throw new InvalidOperationException("unreachable"));
        }
    }

    #endregion

    #region VPAI

    internal class PendingSource : IRepoSource
    {
        [NotNull] private readonly LocalCachedRepository _source;

        public PendingSource([NotNull] LocalCachedRepository source) =>
            _source = source ?? throw new ArgumentNullException(nameof(source));

        public Task<LocalCachedRepository> GetRepo(Environment environment, RepoHolder repoCache) =>
            Task.FromResult(_source);
    }

    public enum AddPackageStatus
    {
        AlreadyAdded, JustAddToDependency, InstallToLocked
    }

    #endregion
    
    delegate bool VersionSelector(Version version);

    static class ModStatics
    {
        public static async Task update_from_remote([CanBeNull] HttpClient client, [NotNull] string path, [NotNull] LocalCachedRepository repo)
        {
            var remoteURL = repo.creation_info?.url;
            if (remoteURL == null) return;

            var foundEtag = repo.vrc_get?.etag;
            try
            {

                var result = await download_remote_repository(client, remoteURL, foundEtag);
                if (result != null)
                {
                    var (remoteRepo, etag) = result.Value;
                    repo.cache = remoteRepo.Get("packages", JsonType.Obj, true) ?? new JsonObj();
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

                    repo.repo = remoteRepo;
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

        public static async Task<(JsonObj, string)?> download_remote_repository(HttpClient client, string url, [CanBeNull] string etag)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (etag != null) {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
            }
            var response = await client.SendAsync(request);
            if (etag != null && response.StatusCode == HttpStatusCode.NotModified)
                return null;
            response.EnsureSuccessStatusCode();

            var newEtag = response.Headers.ETag?.ToString();

            var content = await response.Content.ReadAsStringAsync();
            return (new JsonParser(content).Parse(JsonType.Obj), newEtag);
        }

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
