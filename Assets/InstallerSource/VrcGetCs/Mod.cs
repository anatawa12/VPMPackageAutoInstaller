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
        [CanBeNull] private readonly HttpClient _http;
        [NotNull] private readonly string _globalDir;
        [NotNull] private readonly JsonObj _settings;
        [NotNull] private readonly RepoHolder _repoCache;
        [NotNull] public readonly List<LocalCachedRepository> PendingRepositories = new List<LocalCachedRepository>(); // VPAI
        private bool _settingsDirty;

        private Environment([CanBeNull] HttpClient http, [NotNull] string globalDir, [NotNull] JsonObj settings, [NotNull] RepoHolder repoCache, bool settingsDirty)
        {
            _http = http;
            _globalDir = globalDir;
            _settings = settings;
            _repoCache = repoCache;
            _settingsDirty = settingsDirty;
        }

        public static async Task<Environment> Create(HttpClient http)
        {
            // for macOS, might be changed in .NET 7
            var folder = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            folder = Path.Combine(folder, "VRChatCreatorCompanion");

            //Debug.Log($"initializing Environment with config folder {folder}");

            return new Environment
            (
                http: http,
                settings: await LoadJsonOrElse(Path.Combine(folder, "settings.json"), x => x, () => new JsonObj()),
                globalDir: folder,
                repoCache: new RepoHolder(http),
                settingsDirty: false
            );
        }

        [NotNull]
        public string GetReposDir() => Path.Combine(_globalDir, "Repos");

        [ItemCanBeNull]
        public async Task<PackageJson> FindPackageByName([NotNull] string package, VersionSelector version)
        {
            var versions = await FindPackages(package);

            versions.RemoveAll(x => !version(x.Version));
            versions.Sort((x, y) => y.Version.CompareTo(x.Version));

            return versions.Count != 0 ? versions[0] : null;
        }

        [ItemNotNull]
        public async Task<List<IRepoSource>> GetRepoSources()
        {
            // collect user repositories
            var reposBase = GetReposDir();
            var userRepos = GetUserRepos();

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
                .Select(x => RelativeFileName(x.LocalPath, reposBase))
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
        public async Task<List<LocalCachedRepository>> GetRepos()
        {
            return (await Task.WhenAll((await GetRepoSources()).Select(GetRepo))).ToList();
        }

        [ItemNotNull]
        public Task<LocalCachedRepository> GetRepo(IRepoSource source)
        {
            return source.GetRepo(this, _repoCache);
        }


        [ItemNotNull]
        public async Task<List<PackageJson>> FindPackages([NotNull] string package)
        {
            var list = new List<PackageJson>();

            (await GetRepos())
                .Select(repo => repo.Cache.Get(package, JsonType.Obj, true))
                .Where(x => x != null)
                .Select(json => new PackageVersions(json))
                .SelectMany(x => x.Versions.Values)
                .AddAllTo(list);

            // user package folders
            foreach (var x in GetUserPackageFolders())
            {
                var packageJson =
                    await LoadJsonOrElse(Path.Combine(x, "package.json"), y => new PackageJson(y), () => null);
                if (packageJson != null && packageJson.Name == package) {
                    list.Add(packageJson);
                }
            }

            return list;
        }
        
        [ItemNotNull]
        public async Task<List<PackageJson>> FindWholeAllPackages([NotNull] Func<PackageJson, bool> filter)
        {
            var list = new List<PackageJson>();

            //[CanBeNull] // C# 9.0
            PackageJson GetLatest(PackageVersions versions) =>
                versions
                    .Versions
                    .Values
                    .Where(x => !x.Version.IsPreRelease)
                    .MaxBy(x => x.Version);

            (await GetRepos())
                .SelectMany(x => x.Cache.Select(y => y.Item2))
                .Select(x => new PackageVersions((JsonObj)x))
                .Select(GetLatest)
                .Where(x => x != null)
                .Where(filter)
                .AddAllTo(list);

            // user package folders
            foreach (var x in GetUserPackageFolders())
            {
                var packageJson = await LoadJsonOrElse(Path.Combine(x, "package.json"), y => new PackageJson(y), () => null);
                if (packageJson != null && filter(packageJson))
                {
                    list.Add(packageJson);
                }
            }

            list.Sort((x, y) => y.Version.CompareTo(x.Version));

            return list.DistinctBy(x => (x.Name, x.Version)).ToList();
        }

        public async Task AddPackage([NotNull] PackageJson package, [NotNull] string targetPackagesFolder)
        {
            var zipFileName = $"vrc-get-{package.Name}-{package.Version}.zip";
            var zipPath = Path.Combine(GetReposDir(), package.Name, zipFileName);
            Directory.CreateDirectory(Path.Combine(GetReposDir(), package.Name));
            var shaPath = Path.Combine(GetReposDir(), package.Name, $"{zipFileName}.sha256");
            var destDir = Path.Combine(targetPackagesFolder, package.Name);

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
                    if (_http == null)
                        throw new IOException("Offline mode");

                    zipFile = File.Open(zipPath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                    zipFile.Position = 0;

                    var response = await _http.GetAsync(package.Url, HttpCompletionOption.ResponseHeadersRead);
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
        public List<UserRepoSetting> GetUserRepos() =>
            _settings.Get("userRepos", JsonType.List, true)
                ?.Cast<JsonObj>()
                ?.Select(x => new UserRepoSetting(x))
                ?.ToList() ?? new List<UserRepoSetting>();

        [ItemNotNull]
        [NotNull]
        List<string> GetUserPackageFolders() =>
            _settings.Get("userPackageFolders", JsonType.List, true)?.Cast<string>()?.ToList() ?? new List<string>();

        void AddUserRepo([NotNull] UserRepoSetting repo)
        {
            _settings.GetOrPut("userRepos", () => new List<object>(), JsonType.List)
                .Add(repo.ToJson());
            _settingsDirty = true;
        }

        public async Task AddRemoteRepo([NotNull] string url, [CanBeNull] string name)
        {
            if (GetUserRepos().Any(x => x.URL == url))
                throw new VrcGetException("Already Added");
            if (_http == null)
                throw new OfflineModeException();


            var response = await DownloadRemoteRepository(_http, url, null);
            Debug.Assert(response != null, nameof(response) + " != null");
            var (remoteRepo, etag) = response.Value;
            var localPath = Path.Combine(GetReposDir(), $"{Guid.NewGuid()}.json");

            if (name == null)
                name = remoteRepo.Get("name", JsonType.String, true);

            
            var localCache = new LocalCachedRepository(localPath, name, url);
            localCache.Cache = remoteRepo.Get("packages", JsonType.Obj, true) ?? new JsonObj();
            localCache.Repo = remoteRepo;
            // set etag
            if (etag != null) {
                if (localCache.VrcGet == null)
                    localCache.VrcGet = new VrcGetMeta();
                localCache.VrcGet.Etag = etag;
            }
            await WriteRepo(localPath, localCache);

            AddUserRepo(new UserRepoSetting(localPath, name, url));
        }

        #region VPAI

        public async Task AddPendingRepository([NotNull] string url, [CanBeNull] string name)
        {
            if (GetUserRepos().Any(x => x.URL == url))
                return; // allow already added
            if (_http == null)
                throw new OfflineModeException();

            var response = await DownloadRemoteRepository(_http, url, null);
            Debug.Assert(response != null, nameof(response) + " != null");
            var (remoteRepo, etag) = response.Value;
            var localPath = $"com.anatawa12.vpai.virtually-added.{Guid.NewGuid()}.json";

            if (name == null)
                name = remoteRepo.Get("name", JsonType.String, true);

            var localCache = new LocalCachedRepository(localPath, name, url);
            localCache.Cache = remoteRepo.Get("packages", JsonType.Obj, true) ?? new JsonObj();
            localCache.Repo = remoteRepo;
            // set etag
            if (etag != null) {
                if (localCache.VrcGet == null)
                    localCache.VrcGet = new VrcGetMeta();
                localCache.VrcGet.Etag = etag;
            }
            PendingRepositories.Add(localCache);
        }

        public async Task SavePendingRepositories()
        {
            for (var i = PendingRepositories.Count - 1; i >= 0; i--)
            {
                var localCache = PendingRepositories[i];
                PendingRepositories.RemoveAt(i);

                var localPath = Path.Combine(GetReposDir(), $"{Guid.NewGuid()}.json");
                await WriteRepo(localPath, localCache);
                Debug.Assert(localCache.CreationInfo != null, "localCache.CreationInfo != null");
                localCache.CreationInfo.LocalPath = localPath;
                var name = localCache.CreationInfo.Name;
                var url = localCache.CreationInfo.URL;
                AddUserRepo(new UserRepoSetting(localPath, name, url));                
            }
        }

        #endregion

        public Task AddLocalRepo([NotNull] string path, [CanBeNull] string name)
        {
            if (GetUserRepos().Any(x => x.LocalPath == path))
                throw new VrcGetException("Already Added");

            AddUserRepo(new UserRepoSetting(path, name, null));

            return Task.CompletedTask;
        }

        public Task<bool> RemoveRepo(Func<UserRepoSetting, bool> condition)
        {
            var removes = GetUserRepos()
                .Select((x, i) => (x, i))
                .Where(x => condition(x.x))
                .ToList();
            removes.Reverse();
            if (removes.Count == 0) return Task.FromResult(false);

            var userRepos = _settings.Get("userRepos", JsonType.List);
            for (var i = 0; i < removes.Count; i++)
                userRepos.RemoveAt(removes[i].i);

            foreach (var (x, _) in removes)
                File.Delete(x.LocalPath);
            return Task.FromResult(true);
        }

        public async Task Save()
        {
            if (!_settingsDirty) return;

            await Task.Run(() =>
            {
                Directory.CreateDirectory(_globalDir);
                File.WriteAllText(Path.Combine(_globalDir, "settings.json"), JsonWriter.Write(_settings));
                _settingsDirty = false;
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

        public IReadOnlyDictionary<string, VpmDependency> Dependencies => _dependencies;
        public IReadOnlyDictionary<string, VpmLockedDependency> Locked => _locked;

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

        public void AddDependency(string name, VpmDependency dependency)
        {
            _dependencies[name] = dependency;
            AddToJson("dependencies", name, dependency.ToJson());
        }

        public void AddLocked(string name, VpmLockedDependency dependency)
        {
            _locked[name] = dependency;
            AddToJson("locked", name, dependency.ToJson());
        }

        private void AddToJson(string category, string name, JsonObj jsonObj)
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

        public static async Task<UnityProject> FindUnityProject([NotNull] string unityProject)
        {
            // removed find support
            var unityFound = unityProject; //?? findUnityProjectPath();

            unityFound = Path.Combine(unityFound, "Packages");
            var manifest = Path.Combine(unityFound, "vpm-manifest.json");
            var vpmManifest = new VpmManifest(await LoadJsonOrElse(manifest, x => x, () => new JsonObj()));

            var unlockedPackages = new List<(string, PackageJson)>();

            foreach (var dir in await Task.Run(() => Directory.GetDirectories(unityFound)))
            {
                var read = await TryReadUnlockedPackage(dir, Path.Combine(unityFound, dir), vpmManifest);
                if (read != null)
                    unlockedPackages.Add(read.Value);
            }

            return new UnityProject(unityFound, vpmManifest, unlockedPackages);
        }

        private static async Task<(string, PackageJson)?> TryReadUnlockedPackage(string name, string path,
            VpmManifest vpmManifest)
        {
            var packageJsonPath = Path.Combine(path, "package.json");
            var parsed = await LoadJsonOrElse(packageJsonPath, x => new PackageJson(x), () => null);
            if (parsed != null && parsed.Name == name && vpmManifest.Locked.ContainsKey(name))
                return null;
            return (name, parsed);
        }

        // no findUnityProjectPath

        #region VPAI

        public AddPackageStatus CheckAddPackage(PackageJson request)
        {
            if (_manifest.Dependencies.TryGetValue(request.Name, out var dependency) &&
                dependency.Version >= request.Version)
                return AddPackageStatus.AlreadyAdded;
            if (_manifest.Locked.TryGetValue(request.Name, out var locked) && 
                locked.Version >= request.Version)
                return AddPackageStatus.JustAddToDependency;
            return AddPackageStatus.InstallToLocked;
        }

        #endregion

        public async Task AddPackage(Environment env, PackageJson request)
        {
            if (_manifest.Dependencies.TryGetValue(request.Name, out var dependency) &&
                dependency.Version >= request.Version)
                throw new VrcGetException("AlreadyNewerPackageInstalled");
            if (_manifest.Locked.TryGetValue(request.Name, out var locked) && 
                locked.Version >= request.Version)
            {
                _manifest.AddDependency(request.Name, new VpmDependency(request.Version));
                return;
            }

            List<PackageJson> packages = await CollectAddingPackages(env, request);
            packages.Add(request);

            CheckAddingPackages(packages);

            _manifest.AddDependency(request.Name, new VpmDependency(request.Version));

            await DoAddPackagesToLocked(env, packages);
        }

        // VPAI: make public
        public void CheckAddingPackages(IEnumerable<PackageJson> packages)
        {
            foreach (var package in packages)
                CheckConflict(package.Name, package.Version);
        }

        // VPAI: make public
        public async Task DoAddPackagesToLocked(Environment env, List<PackageJson> packages)
        {
            foreach (var pkg in packages)
                _manifest.AddLocked(pkg.Name, new VpmLockedDependency(pkg.Version, pkg.VpmDependencies));

            await Task.WhenAll(packages.Select(x => env.AddPackage(x, _packagesDir)));
        }

        // no UpgradePackage: VPAI only does adding package.
        // no Remove: VPAI only does adding package
        // no MarkAndSweep

        // VPAI: make public
        public async Task<List<PackageJson>> CollectAddingPackages(Environment env, params PackageJson[] packages)
        {
            var allDeps = new List<PackageJson>();
            foreach (var packageJson in packages)
                await CollectAddingPackagesInternal(allDeps, env, packageJson);
            // ReSharper disable once ForCanBeConvertedToForeach
            // size of all_deps will increase in the loop
            for (var i = 0; i < allDeps.Count; i++)
                await CollectAddingPackagesInternal(allDeps, env, allDeps[i]);
            return allDeps;
        }

        private async Task CollectAddingPackagesInternal([ItemNotNull] List<PackageJson> addingDeps, Environment env, PackageJson pkg)
        {
            foreach (var (dep, range) in pkg.VpmDependencies)
            {
                var installed = _manifest.Locked.TryGetValue(dep, out var locked) ? locked.Version : null;
                if (installed == null || !range.IsSatisfied(installed))
                {
                    var found = await env.FindPackageByName(dep, v => range.IsSatisfied(v));
                    if (found == null)
                        throw new VrcGetException($"Dependency ({dep}) Not Found");
                    addingDeps.Add(found);
                }
            }
        }

        private void CheckConflict(string name, Version version)
        {
            foreach (var (pkgName, dependencies) in AllDependencies())
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

                if (packageJson.Name == name)
                {
                    throw new VrcGetException($"Conflicts with unlocked package: {name}");
                }
            }
        }

        public async Task Save()
        {
            await _manifest.SaveTo(Path.Combine(_packagesDir, "vpm-manifest.json"));
        }
        
        // no Resolve: VPAI only does installing packages

        // locked_packages

        internal IEnumerable<(string, Dictionary<string, VersionRange>)> AllDependencies()
        {
            var lockedDependencies = _manifest.Locked.Select(kvp => (kvp.Key, kvp.Value.Dependencies));
            var unlockedDependencies = _unlockedPackages.Where(x => x.manifest != null)
                .Select(x => (x.manifest.Name, x.manifest.VpmDependencies));

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
            return await repoCache.GetOrCreateRepo(Path.Combine(environment.GetReposDir(), FileName), URL, Name);
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
            return await repoCache.GetUserRepo(Setting);
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
            return await repoCache.GetRepo(Path, () => throw new InvalidOperationException("unreachable"));
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
        public static async Task UpdateFromRemote([CanBeNull] HttpClient client, [NotNull] string path, [NotNull] LocalCachedRepository repo)
        {
            var remoteURL = repo.CreationInfo?.URL;
            if (remoteURL == null) return;

            var foundEtag = repo.VrcGet?.Etag;
            try
            {

                var result = await DownloadRemoteRepository(client, remoteURL, foundEtag);
                if (result != null)
                {
                    var (remoteRepo, etag) = result.Value;
                    repo.Cache = remoteRepo.Get("packages", JsonType.Obj, true) ?? new JsonObj();
                    // set etag
                    if (etag != null)
                    {
                        if (repo.VrcGet == null) repo.VrcGet = new VrcGetMeta();
                        repo.VrcGet.Etag = etag;
                    }
                    else
                    {
                        if (repo.VrcGet != null) repo.VrcGet.Etag = null;
                    }

                    repo.Repo = remoteRepo;
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
                await WriteRepo(path, repo);
            }
            catch (Exception e)
            {
                Debug.LogError($"writing local repo '{path}'");
                Debug.LogException(e);
            }
        }

        public static async Task WriteRepo([NotNull] string path, [NotNull] LocalCachedRepository repo)
        {
            await Task.Run(() =>
            {
                var dir = Path.GetDirectoryName(path);
                Debug.Assert(dir != null, nameof(dir) + " != null");
                Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonWriter.Write(repo.ToJson()));
            });
        }

        public static async Task<(JsonObj, string)?> DownloadRemoteRepository(HttpClient client, string url, [CanBeNull] string etag)
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
        public static Task<FileStream> TryOpenFile([NotNull] string path)
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

        public static async Task<T> LoadJsonOrElse<T>(string manifestPath, Func<JsonObj, T> parser, Func<T> @default)
        {
            var file = await TryReadFile(manifestPath);
            if (file == null) return @default();
            if (file.StartsWith("\uFEFF", StringComparison.Ordinal))
                file = file.Substring(1);

            return parser(new JsonParser(file).Parse(JsonType.Obj));
        }

        public static async Task<T> LoadJsonOrDefault<T>(string manifestPath, Func<JsonObj, T> parser) where T : new()
        {
            return await LoadJsonOrElse(manifestPath, parser, () => new T());
        }
    }
}
