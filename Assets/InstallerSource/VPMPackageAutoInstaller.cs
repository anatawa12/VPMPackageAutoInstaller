/*
 * This is a part of https://github.com/anatawa12/VPMPackageAutoInstaller.
 * 
 * MIT License
 * 
 * Copyright (c) 2022 anatawa12
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using UnityEditor;
using Debug = UnityEngine.Debug;

[assembly: InternalsVisibleTo("com.anatawa12.vpm-package-auto-installer.tester")]

namespace Anatawa12.VpmPackageAutoInstaller
{
    [InitializeOnLoad]
    public static class VpmPackageAutoInstaller
    {
        private const string ConfigGuid = "9028b92d14f444e2b8c389be130d573f";

        private static readonly string[] ToBeRemoved =
        {
            ConfigGuid,
            // the dll file
            "93e23fe9bbc86463a9790ebfd1fef5eb",
            // the folder
            "4b344df74d4849e3b2c978b959abd31b",
        };

        static VpmPackageAutoInstaller()
        {
#if UNITY_5_3_OR_NEWER
            Debug.Log("Unity Compilation Env. Skipping. You should see actual run from compiled dll");
#else
            DoAutoInstall();
#endif
        }

        private static async void DoAutoInstall()
        {
            if (IsDevEnv())
            {
                Debug.Log("In dev env. skipping auto install & remove self");
                return;
            }

            await Task.Delay(1);

            bool installSuccessfull = DoInstall();

            RemoveSelf();

            if (!installSuccessfull)
                AssetDatabase.Refresh();
        }

        internal static bool IsDevEnv()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup buildTargetGroup = BuildPipeline.GetBuildTargetGroup(target);
            return PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup)
                .Contains("VPM_PACKAGE_AUTO_INSTALLER_DEV_ENV");
        }

        private enum Progress
        {
            LoadingConfigJson,
            LoadingVpmManifestJson,
            LoadingGlobalSettingsJson,
            DownloadingRepositories,
            DownloadingNewRepositories,
            ResolvingDependencies,
            Prompting,
            SavingRemoteRepositories,
            DownloadingAndExtractingPackages,
            SavingConfigChanges,
            RefreshingUnityPackageManger,
            Finish
        }

        private static void ShowProgress(string message, Progress progress)
        {
            var ratio = (float)progress / (float)Progress.Finish;
            EditorUtility.DisplayProgressBar("VPAI Installer", message, ratio);
        }


        private static bool IsNoPrompt()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            BuildTargetGroup buildTargetGroup = BuildPipeline.GetBuildTargetGroup(target);
            return PlayerSettings.GetScriptingDefineSymbolsForGroup(buildTargetGroup)
                .Contains("VPM_PACKAGE_AUTO_INSTALLER_NO_PROMPT");
        }

        public static bool DoInstall()
        {
            EditorUtility.DisplayProgressBar("VPAI Installer", "Starting Installer...", 0.0f);
            try
            {
                return DoInstallImpl();
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                EditorUtility.DisplayDialog("ERROR", "Error installing packages", "ok");
                return false;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        private static bool DoInstallImpl()
        {
            ShowProgress("Loading VPAI config...", Progress.LoadingConfigJson);
            var configJson = AssetDatabase.GUIDToAssetPath(ConfigGuid);
            if (string.IsNullOrEmpty(configJson))
            {
                EditorUtility.DisplayDialog("ERROR", "config.json not found. installing package failed.", "ok");
                return false;
            }

            if (!NativeUtils.Call(File.ReadAllBytes(configJson)))
                return false;

            ShowProgress("Refreshing Unity Package Manager...", Progress.RefreshingUnityPackageManger);
            ResolveUnityPackageManger();

            ShowProgress("Almost done!", Progress.Finish);
            return true;
        }

        internal static void ResolveUnityPackageManger()
        {
            System.Reflection.MethodInfo method = typeof(UnityEditor.PackageManager.Client).GetMethod("Resolve",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly);
            if (method != null)
                method.Invoke(null, null);
        }

        public static void RemoveSelf()
        {
            foreach (var remove in ToBeRemoved)
            {
                RemoveFileAsset(remove);
            }

            BurstPatch.UpdateBurstAssemblyFolders();
        }

        private static void RemoveFileAsset(string guid)
        {
            var path = AssetDatabase.GUIDToAssetPath(guid);
            if (File.Exists(path))
            {
                SetDirty(path);
                try
                {
                    File.Delete(path);
                    File.Delete(path + ".meta");
                }
                catch (IOException e)
                {
                    Debug.LogError($"error removing installer: {e}");
                }
            }
            else if (Directory.Exists(path))
            {
                SetDirty(path);
                try
                {
                    Directory.Delete(path);
                    File.Delete(path + ".meta");
                }
                catch (IOException e)
                {
                    Debug.LogError($"error removing installer: {e}");
                }
            }
        }

        private static void SetDirty(string path)
        {
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
            if (asset != null) EditorUtility.SetDirty(asset);
        }
    }

    static class NativeUtils
    {
        private static readonly List<GCHandle> FixedKeep = new List<GCHandle>();

        public static bool Call(byte[] bytes)
        {
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);

            var data = new NativeCsData
            {
                version = 1,
                version_mismatch = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.version_mismatch),

                config_ptr = handle.AddrOfPinnedObject(),
                config_len = bytes.Length,

                display_dialog = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.display_dialog),
                log_error = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.log_error),
                guid_to_asset_path = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.guid_to_asset_path),
                free_cs_memory = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.free_cs_memory),
                verify_url = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.verify_url),
                web_client_new = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.web_client_new),
                web_request_new_get = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.web_request_new_get),
                web_request_add_header = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.web_request_add_header),
                web_request_send = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.web_request_send),
                web_response_status = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.web_response_status),
                web_response_headers = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.web_response_headers),
                web_response_async_reader = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.web_response_async_reader),
                web_response_bytes_async = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.web_response_bytes_async),
                web_headers_get = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.web_headers_get),
                web_async_reader_read = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.web_async_reader_read),
                async_unzip = Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.async_unzip),
            };

            try
            {
                // TODO find dll based on platform
                var path = AssetDatabase.GUIDToAssetPath("e22ab70e285a450ab4bca5c1bddc0bae");
                if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("lib not found");
                using (var lib = LibLoader.LibLoader.LoadLibrary(path))
                {
                    var ptr = lib.GetAddress("vpai_native_entry_point");
                    Debug.Log($"found vpai_native_entry_point: {ptr}");

                    var nativeEntryPoint = Marshal.GetDelegateForFunctionPointer<vpai_native_entry_point_t>(ptr);

                    return nativeEntryPoint(data);
                }
            }
            finally
            {

                while (FixedKeep.Count != 0)
                {
                    FixedKeep[FixedKeep.Count - 1].Free();
                    FixedKeep.RemoveAt(FixedKeep.Count - 1);
                }
            }
        }

        public static IntPtr NewHandle<T>(T handle, GCHandleType type = GCHandleType.Normal) where T : class
        {
            return GCHandle.ToIntPtr(GCHandle.Alloc(handle, type));
        }

        public static T HandleRef<T>(IntPtr handle) where T : class
        {
            return (T)GCHandle.FromIntPtr(handle).Target;
        }

        // own the handle and free
        public static T OwnHandle<T>(IntPtr handle) where T : class
        {
            var gcHandle = GCHandle.FromIntPtr(handle);
            var message = (T)gcHandle.Target;
            gcHandle.Free();
            return message;
        }

        static unsafe class UnsafeCallbacks
        {
            private static void VersionMismatch()
            {
                EditorUtility.DisplayDialog("ERROR", "VPAI Internal Error! Installer is broken or THIS IS A BUG!",
                    "OK");
            }

            private static bool DisplayDialog(in RsSlice title, in RsSlice message, in RsSlice ok, in RsSlice cancel)
            {
                return EditorUtility.DisplayDialog(title.AsString(), message.AsString(), ok.AsString(),
                    cancel.AsString());
            }

            private static void LogError(in RsSlice message)
            {
                Debug.LogError(message.AsString());
            }

            private static void GuidToAssetPath(in RustGUID guid, out CsSlice path)
            {
                var chars = new char[128 / 4];
                for (int i = 0; i < 128 / 8; i++)
                {
                    var b = guid.bytes[i];
                    chars[i * 2 + 0] = "0123456789abcdef"[b >> 4];
                    chars[i * 2 + 0] = "0123456789abcdef"[b & 0xF];
                }

                path = CsSlice.Of(AssetDatabase.GUIDToAssetPath(new string(chars)));
            }

            private static void FreeCsMemory(IntPtr handle)
            {
                if (IntPtr.Zero != handle) OwnHandle<object>(handle);
            }

            private static bool VerifyUrl(in RsSlice url)
            {
                try
                {
                    var parsed = new Uri(url.AsString());
                    return parsed.Scheme == "http" || parsed.Scheme == "https";
                }
                catch
                {
                    return false;
                }
            }

            private static IntPtr WebClientNew(in RsSlice version)
            {
                var client = new HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", 
                    "VpmPackageAutoInstaller/0.3 (github:anatawa12/VpmPackageAutoInstaller) " +
                    $"vrc-get/{version.AsString()} (github:anatawa12/vrc-get VPAI fork)");
                return NewHandle<HttpClient>(client);
            }

            private static IntPtr WebRequestNewGet(IntPtr handle, in RsSlice url) =>
                NewHandle(new WebRequest(HandleRef<HttpClient>(handle),
                    new HttpRequestMessage(HttpMethod.Get, url.AsString())));

            private static void WebRequestAddHeader(IntPtr handle, in RsSlice name, in RsSlice value, out CsErr err)
            {
                try
                {
                    err = default;
                    HandleRef<WebRequest>(handle).Request.Headers.Add(name.AsString(), value.AsString());
                }
                catch (Exception e)
                {
                    err = CsErr.Of(e);
                }
            }

            private static void WebRequestSend(IntPtr handle, IntPtr* result, CsErr* err, IntPtr context,
                IntPtr callback)
                => AsyncCallbacks.WebRequestSend(handle, (IntPtr)result, (IntPtr)err, context, callback);

            private static uint WebResponseStatus(IntPtr handle) =>
                (uint)HandleRef<HttpResponseMessage>(handle).StatusCode;

            private static IntPtr WebResponseHeaders(IntPtr handle) =>
                NewHandle<HttpResponseHeaders>(HandleRef<HttpResponseMessage>(handle).Headers);

            private static IntPtr WebResponseAsyncReader(IntPtr handle) =>
                // important: handle is not ref: rust throw away the ownership
                NewHandle<AsyncReader>(new AsyncReader(() =>
                    OwnHandle<HttpResponseMessage>(handle).Content.ReadAsStreamAsync()));

            private static void WebResponseBytesAsync(IntPtr handle, CsSlice* slice, CsErr* err, IntPtr context,
                IntPtr callback) =>
                AsyncCallbacks.WebResponseBytesAsync(handle, (IntPtr)slice, (IntPtr)err, context, callback);

            private static void WebHeadersGet(IntPtr handle, in RsSlice name, out CsSlice header)
            {
                var first = HandleRef<HttpResponseHeaders>(handle).GetValues(name.AsString()).FirstOrDefault();
                header = first == null ? default : CsSlice.Of(first);
            }

            private static void WebAsyncReaderRead(IntPtr handle, CsSlice* slice, CsErr* err, IntPtr context,
                IntPtr callback) =>
                AsyncCallbacks.WebAsyncReaderRead(handle, (IntPtr)slice, (IntPtr)err, context, callback);

            private static void AsyncUnzip(IntPtr fileHandle, in RsSlice destDir, CsErr* err, IntPtr context,
                IntPtr callback) =>
                AsyncCallbacks.AsyncUnzip(fileHandle, destDir, (IntPtr)err, context, callback);

            // ReSharper disable InconsistentNaming
            public static readonly NativeCsData.version_mismatch_t version_mismatch = VersionMismatch;
            public static readonly NativeCsData.display_dialog_t display_dialog = DisplayDialog;
            public static readonly NativeCsData.log_error_t log_error = LogError;
            public static readonly NativeCsData.guid_to_asset_path_t guid_to_asset_path = GuidToAssetPath;
            public static readonly NativeCsData.free_cs_memory_t free_cs_memory = FreeCsMemory;
            public static readonly NativeCsData.verify_url_t verify_url = VerifyUrl;
            public static readonly NativeCsData.web_client_new_t web_client_new = WebClientNew;
            public static readonly NativeCsData.web_request_new_get_t web_request_new_get = WebRequestNewGet;
            public static readonly NativeCsData.web_request_add_header_t web_request_add_header = WebRequestAddHeader;
            // important: not ref: rust throw away the ownership
            public static readonly NativeCsData.web_request_send_t web_request_send = WebRequestSend;
            public static readonly NativeCsData.web_response_status_t web_response_status = WebResponseStatus;
            public static readonly NativeCsData.web_response_headers_t web_response_headers = WebResponseHeaders;
            // important: handle is not ref: rust throw away the ownership
            public static readonly NativeCsData.web_response_async_reader_t web_response_async_reader = WebResponseAsyncReader;
            // important: handle is not ref: rust throw away the ownership
            public static readonly NativeCsData.web_response_bytes_async_t web_response_bytes_async = WebResponseBytesAsync;
            public static readonly NativeCsData.web_headers_get_t web_headers_get = WebHeadersGet;
            public static readonly NativeCsData.web_async_reader_read_t web_async_reader_read = WebAsyncReaderRead;
            public static readonly NativeCsData.async_unzip_t async_unzip = AsyncUnzip;
            // ReSharper restore InconsistentNaming
        }

        static class AsyncCallbacks
        {
            // wrapper for async method
            private static void Async(IntPtr err, IntPtr context, IntPtr callback, Func<Task> f)
            {
                var task = Task.Run(async () =>
                {
                    try
                    {
                        await f();
                    }
                    catch (Exception e)
                    {
                        unsafe { *(CsErr *)err = CsErr.Of(e); }
                    }
                });
                var awaiter = task.ConfigureAwait(false).GetAwaiter();
                if (awaiter.IsCompleted)
                    Marshal.GetDelegateForFunctionPointer<NativeCsData.async_callback_t>(callback)(context);
                else
                    awaiter.OnCompleted(() =>
                        Marshal.GetDelegateForFunctionPointer<NativeCsData.async_callback_t>(callback)(context));
            }

            public static void WebRequestSend(IntPtr handle, IntPtr result, IntPtr err, IntPtr context,
                IntPtr callback)
            {
                // important: not ref: rust throw away the ownership
                Async(err, context, callback, async () =>
                {
                    var request = OwnHandle<WebRequest>(handle);
                    var wait = await request.Client.SendAsync(request.Request);
                    unsafe
                    {
                        *(IntPtr*)result = NewHandle<HttpResponseMessage>(wait);
                    }
                });
            }

            public static void WebResponseBytesAsync(IntPtr handle, IntPtr slice, IntPtr err, IntPtr context, IntPtr callback)
            {
                Async(err, context, callback, async () =>
                {
                    var stream = await HandleRef<HttpResponseMessage>(handle).Content.ReadAsByteArrayAsync();

                    unsafe
                    {
                        *(CsSlice*)slice = CsSlice.Of(stream);
                    }
                });
            }

            public static void WebAsyncReaderRead(IntPtr handle, IntPtr slice, IntPtr err, IntPtr context, IntPtr callback)
            {
                Async(err, context, callback, async () =>
                {
                    var stream = await HandleRef<AsyncReader>(handle).Task;

                    var bytes = new byte[1024 * 4];
                    var size = await stream.ReadAsync(bytes, 0, bytes.Length);

                    unsafe
                    {
                        *(CsSlice*)slice = CsSlice.Of(bytes, 0, size);
                    }
                });
            }

            public static void AsyncUnzip(IntPtr fileHandle, RsSlice destDir, IntPtr err, IntPtr context, IntPtr callback)
            {
                Async(err, context, callback, async () =>
                {
                    await Task.Run(() =>
                    {
                        using (var safeFileHandle = new SafeFileHandle(fileHandle, true))
                        using (var fileStream = new FileStream(safeFileHandle, FileAccess.Write))
                        using (var source = new ZipArchive(fileStream, ZipArchiveMode.Read, false, Encoding.UTF8))
                            source.ExtractToDirectory(destDir.AsString());
                    });
                });
            }
        }

        class WebRequest
        {
            public readonly HttpClient Client;
            public readonly HttpRequestMessage Request;

            public WebRequest(HttpClient client, HttpRequestMessage request)
            {
                Client = client;
                Request = request;
            }
        }

        class AsyncReader
        {
            public readonly Task<Stream> Task;

            public AsyncReader(Func<Task<Stream>> func)
            {
                Task = System.Threading.Tasks.Task.Run(func);
            }
        }

        // ReSharper disable InconsistentNaming

        delegate bool vpai_native_entry_point_t(in NativeCsData data);

        unsafe struct NativeCsData
        {
            // ReSharper disable MemberHidesStaticFromOuterClass
            public ulong version;
            public IntPtr version_mismatch;
            // end of version independent part

            // config json info. might not be utf8
            public IntPtr config_ptr;
            public int config_len;
            // config json info. might not be utf8
            public IntPtr display_dialog;
            public IntPtr log_error;
            public IntPtr guid_to_asset_path;
            public IntPtr free_cs_memory;
            public IntPtr verify_url;
            public IntPtr web_client_new;
            public IntPtr web_request_new_get;
            public IntPtr web_request_add_header;
            public IntPtr web_request_send;
            public IntPtr web_response_status;
            public IntPtr web_response_headers;
            public IntPtr web_response_async_reader;
            public IntPtr web_response_bytes_async;
            public IntPtr web_headers_get;
            public IntPtr web_async_reader_read;
            public IntPtr async_unzip;
            // ReSharper restore MemberHidesStaticFromOuterClass


            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void version_mismatch_t();
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate bool display_dialog_t(in RsSlice title, in RsSlice message, in RsSlice ok, in RsSlice cancel);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void log_error_t(in RsSlice messagePtr);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void guid_to_asset_path_t(in RustGUID guid, out CsSlice path);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate bool verify_url_t(in RsSlice guid);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void free_cs_memory_t(IntPtr handle);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate IntPtr web_client_new_t(in RsSlice version);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate IntPtr web_request_new_get_t(IntPtr handle, in RsSlice url);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void web_request_add_header_t(IntPtr handle, in RsSlice name, in RsSlice value, out CsErr err);
            // important: not ref: rust throw away the ownership
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void web_request_send_t(IntPtr handle, IntPtr *result, CsErr *err, IntPtr context, IntPtr callback);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate uint web_response_status_t(IntPtr handle);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate IntPtr web_response_headers_t(IntPtr handle);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            // important: handle is not ref: rust throw away the ownership
            public delegate IntPtr web_response_async_reader_t(IntPtr handle);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void web_response_bytes_async_t(IntPtr handle, CsSlice/*<byte>*/ *slice, CsErr *err, IntPtr context, IntPtr callback);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void web_headers_get_t(IntPtr handle, in RsSlice name, out CsSlice header);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void web_async_reader_read_t(IntPtr handle, CsSlice/*<byte>*/ *slice, CsErr *err, IntPtr context, IntPtr callback);
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void async_unzip_t(IntPtr fileHandle, in RsSlice /*<byte>*/ destDir, CsErr* err, IntPtr context, IntPtr callback);
            
            [UnmanagedFunctionPointer(CallingConvention.Winapi)]
            public delegate void async_callback_t(IntPtr callback);
        }

        unsafe struct RustGUID {
            public fixed byte bytes[128/8];
        }

        // because generic struct is not unmanaged
        readonly unsafe struct CsSlice
        {
            private readonly IntPtr handle;
            private readonly IntPtr ptr; //T *ptr;
            private readonly IntPtr len;

            private CsSlice(IntPtr handle, IntPtr ptr, IntPtr len)
            {
                this.handle = handle;
                this.ptr = ptr;
                this.len = len;
            }

            public static CsSlice Of<T>(T[] array) where T : unmanaged => Of(array, 0, array.Length);

            public static CsSlice Of<T>(T[] array, int offset, int len) where T : unmanaged
            {
                var handle = NewHandle(array);
                return new CsSlice(
                    handle,
                    GCHandle.FromIntPtr(handle).AddrOfPinnedObject() + offset * sizeof(T),
                    (IntPtr) len);
            }
            
            public static CsSlice Of(string str) => Of(UTF8.GetBytes(str));
        }

        readonly struct RsSlice
        {
#pragma warning disable CS0649
            private readonly IntPtr ptr;//T *ptr;
            private readonly IntPtr len;
#pragma warning restore CS0649
            
            public unsafe string AsString()
            {
                if ((ulong) len >= int.MaxValue)
                    throw new InvalidOperationException("str too big to be string");
                return UTF8.GetString((byte *)ptr, (int)len);
            }
        }

        readonly struct CsErr
        {
            public readonly CsSlice str;
            public readonly int as_id;

            private CsErr(CsSlice str, int asID)
            {
                this.str = str;
                as_id = asID;
            }

            public static CsErr Of(Exception exception)
            {
                return new CsErr(
                    str: CsSlice.Of(exception.Message), 
                    asID: exception is IOException ioe ? ioe.HResult : 0);
            }
        }

        private static readonly Encoding UTF8 = new UTF8Encoding(false);
        // ReSharper restore InconsistentNaming

        // only for generic check
        [Conditional("NEVER_DEFINED")]
        private static void EnsureUnmanaged<T>() where T : unmanaged
        {
            EnsureUnmanaged<CsSlice>();
            EnsureUnmanaged<RsSlice>();
        }
    }
}
