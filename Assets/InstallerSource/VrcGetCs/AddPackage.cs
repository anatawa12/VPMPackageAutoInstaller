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

namespace Anatawa12.VrcGet
{
    // add_package.rs in original
    internal static class AddPackage
    {
        public static async Task add_package(
            string global_dir,
            HttpClient http,
            PackageInfo package,
            string target_packages_folder
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
            string global_dir,
            HttpClient http,
            PackageJson package,
            IDictionary<string, string> headers,
            string target_packages_folder
        )
        {
            var zip_file_name = $"vrc-get-{package.name}-{package.version}.zip";
            var zip_path = Path.Combine(global_dir, "Repos", package.name, zip_file_name);
            await create_dir_all(Path.GetDirectoryName(zip_path));
            var sha_path = $"{zip_path}.sha256";
            var dest_folder = Path.Combine(target_packages_folder, package.name);

            // TODO: set sha256 when zipSHA256 is documented
            var zip_file = await try_cache(zip_path, sha_path, null);
            if (zip_file == null)
                zip_file = await download_zip(http, headers, zip_path, sha_path, zip_file_name, package.url);

            // remove dest folder before extract if exists
            try
            {
                await remove_dir_all(dest_folder);
            }
            catch
            {
                // ignored
            }

            // extract zip file
            using (var archive = new ZipArchive(zip_file, ZipArchiveMode.Read, false))
                archive.ExtractToDirectory(dest_folder);
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
        static async Task<FileStream> try_cache([NotNull] string zip_path, [NotNull] string sha_path,
            [CanBeNull] string sha256)
        {

            FileStream result = null;
            FileStream cache_file = null;
            try
            {
                cache_file = File.OpenRead(zip_path);
                using (var sha_file = File.OpenRead(sha_path))
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
            [NotNull] string zip_path,
            [NotNull] string sha_path,
            [NotNull] string zip_file_name,
            [NotNull] string url
        )
        {
            if (http == null)
                throw new IOException("Offline mode");

            FileStream cache_file = null, result = null;

            try
            {
                cache_file = File.Open(zip_path, FileMode.OpenOrCreate, FileAccess.ReadWrite);
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
                File.WriteAllText(sha_path, $"{to_hex(hash)} {zip_file_name}\n");

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

        static async Task add_local_package([NotNull] string package, [NotNull] string name,
            [NotNull] string target_packages_folder)
        {
            var dest_folder = Path.Combine(target_packages_folder, name);
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

        static async Task copy_recursive(string src_dir, string dst_dir)
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
                    string targetFilePath = Path.Combine(destinationDir, file.Name);
                    file.CopyTo(targetFilePath);
                }

                // If recursive and copying subdirectories, recursively call this method

                foreach (DirectoryInfo subDir in dirs)
                    Inner(subDir.FullName, Path.Combine(destinationDir, subDir.Name));
            }

            await Task.Run(() => Inner(src_dir, dst_dir));
        }
    }
}
