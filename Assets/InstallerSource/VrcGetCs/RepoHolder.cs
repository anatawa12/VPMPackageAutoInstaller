using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
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
        [NotNull] readonly ConcurrentDictionary<string, Entry> _cachedRepos;

        class Entry
        {
            public readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
            public LocalCachedRepository Field;
        }

        public RepoHolder([CanBeNull] HttpClient http)
        {
            _http = http;
            _cachedRepos = new ConcurrentDictionary<string, Entry>();
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
            var entry = _cachedRepos.GetOrAdd(path, _ => new Entry());
            LocalCachedRepository value;

            // fast path: if no one is holding the semaphore and field is initialized, use it.
            if (entry.Semaphore.CurrentCount == 1 && (value = Volatile.Read(ref entry.Field)) != null)
                return value;

            await entry.Semaphore.WaitAsync();
            try
            {
                if (entry.Field != null)
                    return entry.Field;

                var text = await TryReadFile(path);
                if (text == null)
                {
                    value = await ifNotFound();
                    Volatile.Write(ref entry.Field, value);
                    return value;
                }
                var loaded = new LocalCachedRepository(new JsonParser(text).Parse(JsonType.Obj));
                if (_http != null)
                    await UpdateFromRemote(_http, path, loaded);

                Volatile.Write(ref entry.Field, loaded);
                return loaded;
            }
            finally
            {
                entry.Semaphore.Release();
            }
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
