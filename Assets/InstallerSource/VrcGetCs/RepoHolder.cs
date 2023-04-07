// ReSharper disable InconsistentNaming

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
        [CanBeNull] private readonly HttpClient http;

        // the pointer of LocalCachedRepository will never be changed
        [NotNull] readonly ConcurrentDictionary<string, Entry> cached_repos;

        class Entry
        {
            public readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
            public LocalCachedRepository Field;
        }

        public RepoHolder([CanBeNull] HttpClient http)
        {
            this.http = http;
            cached_repos = new ConcurrentDictionary<string, Entry>();
        }

        [ItemNotNull]
        public async Task<LocalCachedRepository> get_or_create_repo(
            [NotNull] string path,
            [NotNull] string remoteUrl,
            [CanBeNull] string name)
        {
            return await get_repo(path, async () =>
            {
                var http = this.http ?? throw new IOException("offline mode");
                var result = await download_remote_repository(http, remoteUrl, null);
                System.Diagnostics.Debug.Assert(result != null, nameof(result) + " != null: no etag");
                var (remoteRepo, etag) = result.Value;
                var localCache = new LocalCachedRepository(path, name, remoteUrl);
                localCache.cache = remoteRepo.Get("packages", JsonType.Obj, true);
                localCache.repo = remoteRepo;

                if (etag != null)
                {
                    localCache.vrc_get = localCache.vrc_get ?? new VrcGetMeta();
                    localCache.vrc_get.etag = etag;
                }

                try
                {
                    await write_repo(path, localCache);
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
        internal async Task<LocalCachedRepository> get_repo([NotNull] string path,
            [NotNull] Func<Task<LocalCachedRepository>> ifNotFound)
        {
            var entry = cached_repos.GetOrAdd(path, _ => new Entry());
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
                if (http != null)
                    await update_from_remote(http, path, loaded);

                Volatile.Write(ref entry.Field, loaded);
                return loaded;
            }
            finally
            {
                entry.Semaphore.Release();
            }
        }

        public Task<LocalCachedRepository> get_user_repo(UserRepoSetting repo)
        {
            if (repo.url is string url)
            {
                return get_or_create_repo(repo.local_path, url, repo.name);
            }
            else
            {
                return get_repo(repo.local_path, () => throw new IOException("Repository not found"));
            }
        }
    }
}
