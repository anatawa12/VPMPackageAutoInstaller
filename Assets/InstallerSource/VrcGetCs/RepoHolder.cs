using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Anatawa12.SimpleJson;
using JetBrains.Annotations;
using UnityEngine;
using static Anatawa12.VrcGet.ModStatics;

namespace Anatawa12.VrcGet
{
    class RepoHolder
    {
        [CanBeNull] private readonly HttpClient _http;

        // the pointer of LocalCachedRepository will never be changed
        [NotNull] readonly Dictionary<string, LocalCachedRepository> _cachedRepos;

        public RepoHolder([CanBeNull] HttpClient http)
        {
            _http = http;
            _cachedRepos = new Dictionary<string, LocalCachedRepository>();
        }

        [ItemNotNull]
        public async Task<LocalCachedRepository> GetOrCreateRepo(
            [NotNull] string path,
            [NotNull] string remoteUrl,
            [CanBeNull] string name)
        {
            return await GetRepo(path, async () =>
            {
                var http = _http ?? throw new IOException("offline mode");
                var result = await DownloadRemoteRepository(http, remoteUrl, null);
                System.Diagnostics.Debug.Assert(result != null, nameof(result) + " != null: no etag");
                var (remoteRepo, etag) = result.Value;
                var localCache = new LocalCachedRepository(path, name, remoteUrl);
                localCache.Cache = remoteRepo.Get("packages", JsonType.Obj, true);
                localCache.Repo = remoteRepo;

                if (etag != null)
                {
                    localCache.VrcGet = localCache.VrcGet ?? new VrcGetMeta();
                    localCache.VrcGet.Etag = etag;
                }

                try
                {
                    await WriteRepo(path, localCache);
                }
                catch (Exception e)
                {
                    Debug.LogError("Error writing local repository");
                    Debug.LogException(e);
                }

                return localCache;
            });
        }

        [ItemNotNull]
        internal async Task<LocalCachedRepository> GetRepo([NotNull] string path,
            [NotNull] Func<Task<LocalCachedRepository>> ifNotFound)
        {
            if (_cachedRepos.TryGetValue(path, out var cached))
                return cached;

            var text = await TryReadFile(path);
            if (text == null) 
                return _cachedRepos[path] = await ifNotFound();
            var loaded = new LocalCachedRepository(new JsonParser(text).Parse(JsonType.Obj));
            if (_http != null)
                await UpdateFromRemote(_http, path, loaded);

            return _cachedRepos[path] = loaded;
        }

        public Task<LocalCachedRepository> GetUserRepo(UserRepoSetting repo)
        {
            if (repo.URL is string url)
            {
                return GetOrCreateRepo(repo.LocalPath, url, repo.Name);
            }
            else
            {
                return GetRepo(repo.LocalPath, () => throw new IOException("Repository not found"));
            }
        }
    }
}
