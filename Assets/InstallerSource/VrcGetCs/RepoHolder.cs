// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        // the pointer of LocalCachedRepository will never be changed
        [NotNull] readonly Dictionary<Path, LocalCachedRepository> cached_repos_new;

        public RepoHolder()
        {
            cached_repos_new = new Dictionary<Path, LocalCachedRepository>();
        }

        internal async Task load_repos([CanBeNull] HttpClient http, [NotNull] [ItemNotNull] IEnumerable<RepoSource> sources)
        {
            var repos = await Task.WhenAll(sources.Select(async src =>
                (await load_repo_from_source(http, src), src.file_path())));

            foreach (var (repo, path) in repos)
            {
                cached_repos_new[path] = repo;
            }
        }

        static async Task<LocalCachedRepository> load_repo_from_source([CanBeNull] HttpClient client, [NotNull] RepoSource source)
        {
            return await source.VisitLoadRepo(client);
        }

        public static async Task<LocalCachedRepository> LoadPreDefinedRepo([CanBeNull] HttpClient client,
            [NotNull] PreDefinedRepoSource source)
        {
            return await load_remote_repo(
                client,
                null,
                source.path,
                source.Info.url
            );
        }

        public static async Task<LocalCachedRepository> LoadUserRepo([CanBeNull] HttpClient client,
            [NotNull] UserRepoSource source)
        {
            var user_repo = source.Setting;
            if (user_repo.url is string url)
            {
                return await load_remote_repo(
                    client,
                    user_repo.headers,
                    user_repo.local_path,
                    url
                );
            }
            else
            {
                return await load_local_repo(client, user_repo.local_path);
            }
        }

        static async Task<LocalCachedRepository> load_remote_repo(
            [CanBeNull] HttpClient client,
            [CanBeNull] Dictionary<String, String> headers,
            [NotNull] Path path,
            [NotNull] string remote_url
        )
        {
            return await load_repo(path, client, async () =>
            {
                // if local repository not found: try downloading remote one
                if (client == null) throw new OfflineModeException();

                var may_null = await download_remote_repository(client, remote_url, headers, null);
                System.Diagnostics.Debug.Assert(may_null != null, nameof(may_null) + " != null");
                var (remote_repo, etag) = may_null.Value;

                var local_cache = new LocalCachedRepository(remote_repo, headers ?? new Dictionary<string, string>());

                if (etag != null)
                {
                    local_cache.vrc_get = local_cache.vrc_get ?? new VrcGetMeta();
                    local_cache.vrc_get.etag = etag;
                }

                try
                {
                    await write_repo(path, local_cache);
                }
                catch (Exception e)
                {
                    Debug.LogError("Error writing local repository");
                    Debug.LogException(e);
                }

                return local_cache;
            });
        }

        static async Task<LocalCachedRepository> load_local_repo(
            [CanBeNull] HttpClient client,
            [NotNull] Path path
        )
        {
            return await load_repo(path, client, () => throw new IOException("repository not found"));
        }

        static async Task<LocalCachedRepository> load_repo(
            [NotNull] Path path,
            [CanBeNull] HttpClient http,
            Func<Task<LocalCachedRepository>> if_not_found
        )
        {
            string text;
            try
            {
                text = await TryReadFile(path);
            }
            catch
            {
                return await if_not_found();
            }

            if (text == null)
                return await if_not_found();

            var loaded = new LocalCachedRepository(new JsonParser(text).Parse(JsonType.Obj));
            if (http != null)
                await update_from_remote(http, path, loaded);
            return loaded;
        }

        public LocalCachedRepository[] get_repos() => cached_repos_new.Values.ToArray();

        public IEnumerable<(Path, LocalCachedRepository)> get_repo_with_path() =>
            cached_repos_new.Select(x => (x.Key, x.Value));

        [CanBeNull]
        internal LocalCachedRepository get_repo(Path path) => cached_repos_new.get(path);

        // VPAI: to add pending repo
        public void AddRepository(Path path, LocalCachedRepository cache)
        {
            cached_repos_new.Add(path, cache);
        }

        public void remove_repo(Path path)
        {
            cached_repos_new.Remove(path);
        }
    }
}
