// ReSharper disable InconsistentNaming

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading.Tasks;
using JetBrains.Annotations;
using static Anatawa12.VrcGet.CsUtils;
using SystemPath = System.IO.Path;

namespace Anatawa12.VrcGet
{
    // add_package.rs in original
    internal static class AddPackage
    {
        public static async Task add_package(
            Path global_dir,
            HttpClient http,
            PackageInfo package,
            Path target_packages_folder
        )
        {
            if (package.is_remote())
            {
                await add_remote_package(global_dir, http, package.package_json(), package.remote().headers(),
                    target_packages_folder);
            }
            else
            {
                await add_local_package(package.local(), package.package_json().name, target_packages_folder);
            }
        }

        static async Task add_remote_package(
            Path global_dir,
            HttpClient http,
            PackageJson package,
            IDictionary<string, string> headers,
            Path target_packages_folder
        )
        {
            var zip_file_name = $"vrc-get-{package.name}-{package.version}.zip";
            var zip_path = global_dir
                .joined("Repos")
                .joined(package.name)
                .joined(zip_file_name);
            await create_dir_all(zip_path.parent());
            var sha_path = zip_path.with_extension($"zip.sha256");
            var dest_folder = target_packages_folder.join(package.name);

            // TODO: set sha256 when zipSHA256 is documented
            var zip_file = await try_cache(zip_path, sha_path, null);
            if (zip_file == null)
                zip_file = await download_zip(http, headers, zip_path, sha_path, zip_file_name, package.url);

            // VPAI: overwrite changed files instead of removing dest folder before extract.
            // On Windows, native plugins loaded by Unity (e.g. AudioPluginOculusSpatializer.dll in com.vrchat.base)
            // cannot be deleted. Removing the folder failed halfway and extracting threw on the remaining files,
            // which left the package broken. Files with the same content are not touched, and files in use are
            // moved to Temp (it is on the same volume and Unity clears it on exit) before writing the new ones.
            var trash_folder = SystemPath.Combine(SystemPath.GetDirectoryName(SystemPath.GetFullPath(target_packages_folder.AsString)),
                "Temp", "com.anatawa12.vpm-package-auto-installer", Guid.NewGuid().ToString("N"));

            // extract zip file
            using (var archive = new ZipArchive(zip_file, ZipArchiveMode.Read, false))
                await Task.Run(() => extract_overwriting(archive, dest_folder.AsString, trash_folder));
        }

        static void extract_overwriting(ZipArchive archive, string dest_folder, string trash_folder)
        {
            var root = SystemPath.GetFullPath(dest_folder);
            Directory.CreateDirectory(root);
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in archive.Entries)
            {
                var path = SystemPath.GetFullPath(SystemPath.Combine(root, entry.FullName));
                if (!path.StartsWith(root + SystemPath.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"zip entry is outside of the destination: {entry.FullName}");

                if (entry.Name.Length == 0)
                {
                    // directory entry
                    Directory.CreateDirectory(path);
                    continue;
                }

                Directory.CreateDirectory(SystemPath.GetDirectoryName(path));
                extracted.Add(path);
                if (File.Exists(path) && same_content(entry, path)) continue;

                try
                {
                    write_entry(entry, path);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    move_to_trash(path, root, trash_folder);
                    write_entry(entry, path);
                }
            }

            // remove files which are not in the new version
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                if (extracted.Contains(SystemPath.GetFullPath(file))) continue;
                try
                {
                    File.Delete(file);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    move_to_trash(file, root, trash_folder);
                }
            }

            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                try
                {
                    Directory.Delete(dir);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    // ignored
                }
            }
        }

        static bool same_content(ZipArchiveEntry entry, string path)
        {
            if (new FileInfo(path).Length != entry.Length) return false;
            using (var a = entry.Open())
            using (var b = File.OpenRead(path))
            {
                var bufA = new byte[81920];
                var bufB = new byte[81920];
                while (true)
                {
                    var read = read_fully(a, bufA);
                    if (read != read_fully(b, bufB)) return false;
                    if (read == 0) return true;
                    for (var i = 0; i < read; i++)
                        if (bufA[i] != bufB[i]) return false;
                }
            }
        }

        static int read_fully(Stream stream, byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0) break;
                total += read;
            }
            return total;
        }

        static void write_entry(ZipArchiveEntry entry, string path)
        {
            using (var source = entry.Open())
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                source.CopyTo(file);
            File.SetLastWriteTime(path, entry.LastWriteTime.DateTime);
        }

        // a file loaded by Unity cannot be deleted or overwritten on Windows, but can be moved on the same volume
        static void move_to_trash(string path, string root, string trash_folder)
        {
            var moved = SystemPath.Combine(trash_folder, path.Substring(root.Length + 1));
            Directory.CreateDirectory(SystemPath.GetDirectoryName(moved));
            File.Move(path, moved);
        }

        /// Try to load from the zip file
        /// 
        /// # Arguments 
        /// 
        /// * `zip_path`: the path to zip file
        /// * `sha_path`: the path to sha256 file
        /// * `sha256`: sha256 hash if specified
        /// 
        /// returns: Option<File> readable zip file file or None
        /// 
        /// # Examples 
        /// 
        /// ```
        /// 
        /// ```
        [ItemCanBeNull]
        static async Task<FileStream> try_cache([NotNull] Path zip_path, [NotNull] Path sha_path,
            [CanBeNull] string sha256)
        {

            FileStream result = null;
            FileStream cache_file = null;
            try
            {
                cache_file = File.OpenRead(zip_path.AsString);
                using (var sha_file = File.OpenRead(sha_path.AsString))
                {
                    var buf = new byte[256 / 8];
                    await sha_file.ReadExactAsync(buf);
                    var hex = parse_hex_256_bytes(buf);

                    byte[] hash;

                    var repo_hash = sha256 == null ? null : parse_hex_256_str(sha256);

                    if (repo_hash != null)
                    {
                        if (!repo_hash.SequenceEqual(hex))
                            return null;
                    }

                    using (var sha256hasher = SHA256.Create()) hash = sha256hasher.ComputeHash(cache_file);

                    if (!hash.SequenceEqual(hex)) return null;

                    cache_file.Seek(0, SeekOrigin.Begin);
                    return result = cache_file;
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (cache_file != null && cache_file != result)
                    cache_file.Dispose();
            }

            
            //[CanBeNull]
            byte[] parse_hex_256_str(/*[NotNull]*/ string hex)
            {
                byte ParseChar(char c)
                {
                    if ('0' <= c && c <= '9') return (byte)(c - '0');
                    if ('a' <= c && c <= 'f') return (byte)(c - 'a' + 10);
                    if ('A' <= c && c <= 'F') return (byte)(c - 'A' + 10);
                    return 255;
                }
                var bytes = new byte[hex.Length / 2];
                for (var i = 0; i < bytes.Length; i++)
                {
                    var upper = ParseChar(hex[i * 2 + 0]);
                    var lower = ParseChar(hex[i * 2 + 0]);
                    if (upper == 255 || lower == 255) return null;
                    bytes[i] = (byte)(upper << 4 | lower);
                }
                return bytes;
            }
            
            //[CanBeNull]
            byte[] parse_hex_256_bytes(/*[NotNull]*/ byte[] hex)
            {
                byte ParseChar(byte c)
                {
                    if ('0' <= c && c <= '9') return (byte)(c - '0');
                    if ('a' <= c && c <= 'f') return (byte)(c - 'a' + 10);
                    if ('A' <= c && c <= 'F') return (byte)(c - 'A' + 10);
                    return 255;
                }
                var bytes = new byte[hex.Length / 2];
                for (var i = 0; i < bytes.Length; i++)
                {
                    var upper = ParseChar(hex[i * 2 + 0]);
                    var lower = ParseChar(hex[i * 2 + 0]);
                    if (upper == 255 || lower == 255) return null;
                    bytes[i] = (byte)(upper << 4 | lower);
                }
                return bytes;
            }
        }

        /// downloads the zip file from the url to the specified path 
        /// 
        /// # Arguments 
        /// 
        /// * `http`: http client. returns error if none
        /// * `zip_path`: the path to zip file
        /// * `sha_path`: the path to sha256 file
        /// * `zip_file_name`: the name of zip file. will be used in the sha file
        /// * `url`: url to zip file
        /// 
        /// returns: Result<File, Error> the readable zip file.
        [ItemNotNull]
        static async Task<FileStream> download_zip(
            [CanBeNull] HttpClient http,
            [NotNull] IDictionary<String, String> headers,
            [NotNull] Path zip_path,
            [NotNull] Path sha_path,
            [NotNull] string zip_file_name,
            [NotNull] string url
        )
        {
            if (http == null)
                throw new IOException("Offline mode");

            FileStream cache_file = null, result = null;

            try
            {
                cache_file = File.Open(zip_path.AsString, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                cache_file.Position = 0;

                var request = new HttpRequestMessage(HttpMethod.Get, url);
                foreach (var (key, value) in headers)
                    request.Headers.TryAddWithoutValidation(key, value);

                var response = await http.SendAsync(request);
                response.EnsureSuccessStatusCode();

                var responseStream = await response.Content.ReadAsStreamAsync();

                await responseStream.CopyToAsync(cache_file);

                await cache_file.FlushAsync();
                cache_file.Position = 0;

                byte[] hash;
                using (var sha256 = SHA256.Create()) hash = sha256.ComputeHash(cache_file);
                cache_file.Position = 0;

                // write SHA file
                await WriteAllText(sha_path, $"{to_hex(hash)} {zip_file_name}\n");

                return result = cache_file;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (result != cache_file)
                    cache_file?.Dispose();
            }
        }

        [NotNull]
        static string to_hex([NotNull] byte[] data)
        {
            var result = new char[data.Length * 2];
            for (var i = 0; i < data.Length; i++)
            {
                result[i * 2 + 0] = "0123456789abcdef"[(data[i] >> 4) & 0xf];
                result[i * 2 + 1] = "0123456789abcdef"[(data[i] >> 0) & 0xf];
            }

            return new string(result);
        }

        // no check_path

        static async Task add_local_package([NotNull] Path package, [NotNull] string name,
            [NotNull] Path target_packages_folder)
        {
            var dest_folder = target_packages_folder.join(name);
            try
            {
                await remove_dir_all(dest_folder);
            }
            catch
            {
                // ignored
            }

            await copy_recursive(package, dest_folder);
        }

        static async Task copy_recursive(Path src_dir, Path dst_dir)
        {
            // TODO: parallelize & speedup
            // VPAI: we use actual recursive
            void Inner(string sourceDir, string destinationDir)
            {
                // Get information about the source directory
                var dir = new DirectoryInfo(sourceDir);

                // Check if the source directory exists
                if (!dir.Exists)
                    throw new DirectoryNotFoundException($"Source directory not found: {dir.FullName}");

                // Cache directories before we start copying
                DirectoryInfo[] dirs = dir.GetDirectories();

                // Create the destination directory
                Directory.CreateDirectory(destinationDir);

                // Get the files in the source directory and copy to the destination directory
                foreach (FileInfo file in dir.GetFiles())
                {
                    string targetFilePath = System.IO.Path.Combine(destinationDir, file.Name);
                    file.CopyTo(targetFilePath);
                }

                // If recursive and copying subdirectories, recursively call this method

                foreach (DirectoryInfo subDir in dirs)
                    Inner(subDir.FullName, System.IO.Path.Combine(destinationDir, subDir.Name));
            }

            await Task.Run(() => Inner(src_dir.AsString, dst_dir.AsString));
        }
    }
}
