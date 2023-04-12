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
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using UnityEditor;
using Debug = UnityEngine.Debug;
using static Anatawa12.VpmPackageAutoInstaller.NativeUtils;

namespace Anatawa12.VpmPackageAutoInstaller
{
    static class NativeUtils
    {
        private static readonly List<GCHandle> FixedKeep = new List<GCHandle>();

        public static unsafe bool Call(byte[] bytes)
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
                web_response_async_reader =
                    Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.web_response_async_reader),
                web_response_bytes_async =
                    Marshal.GetFunctionPointerForDelegate(UnsafeCallbacks.web_response_bytes_async),
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

                    var nativeEntryPoint = Marshal.GetDelegateForFunctionPointer<Ptr1ToBool>(ptr);

                    return nativeEntryPoint((IntPtr)(&data));
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

        internal static readonly Encoding UTF8 = new UTF8Encoding(false);
    }

    static unsafe class UnsafeCallbacks
    {
        private static void VersionMismatch()
        {
            EditorUtility.DisplayDialog("ERROR", "VPAI Internal Error! Installer is broken or THIS IS A BUG!",
                "OK");
        }

        private static bool DisplayDialog(IntPtr title, IntPtr message, IntPtr ok, IntPtr cancel)
        {
            return EditorUtility.DisplayDialog((*(RsSlice*)title).AsString(), (*(RsSlice*)message).AsString(),
                (*(RsSlice*)ok).AsString(), (*(RsSlice*)cancel).AsString());
        }

        private static void LogError(IntPtr message)
        {
            Debug.LogError((*(RsSlice*)message).AsString());
        }

        private static void GuidToAssetPath(IntPtr guid, IntPtr path)
        {
            var chars = new char[128 / 4];
            for (int i = 0; i < 128 / 8; i++)
            {
                var b = ((RustGUID*)guid)->bytes[i];
                chars[i * 2 + 0] = "0123456789abcdef"[b >> 4];
                chars[i * 2 + 0] = "0123456789abcdef"[b & 0xF];
            }

            (*(CsSlice*)path) = CsSlice.Of(AssetDatabase.GUIDToAssetPath(new string(chars)));
        }

        private static void FreeCsMemory(IntPtr handle)
        {
            if (IntPtr.Zero != handle) OwnHandle<object>(handle);
        }

        private static bool VerifyUrl(IntPtr url)
        {
            try
            {
                var parsed = new Uri((*(RsSlice*)url).AsString());
                return parsed.Scheme == "http" || parsed.Scheme == "https";
            }
            catch
            {
                return false;
            }
        }

        private static IntPtr WebClientNew(IntPtr version)
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Add("User-Agent",
                "VpmPackageAutoInstaller/0.3 (github:anatawa12/VpmPackageAutoInstaller) " +
                $"vrc-get/{(*(RsSlice*)version).AsString()} (github:anatawa12/vrc-get VPAI fork)");
            return NewHandle<HttpClient>(client);
        }

        private static IntPtr WebRequestNewGet(IntPtr handle, IntPtr url) =>
            NewHandle(new WebRequest(HandleRef<HttpClient>(handle),
                new HttpRequestMessage(HttpMethod.Get, (*(RsSlice*)url).AsString())));

        private static void WebRequestAddHeader(IntPtr handle, IntPtr name, IntPtr value, IntPtr err)
        {
            try
            {
                *(CsErr*)err = default;
                HandleRef<WebRequest>(handle).Request.Headers
                    .Add((*(RsSlice*)name).AsString(), (*(RsSlice*)value).AsString());
            }
            catch (Exception e)
            {
                *(CsErr*)err = CsErr.Of(e);
            }
        }

        private static uint WebResponseStatus(IntPtr handle) =>
            (uint)HandleRef<HttpResponseMessage>(handle).StatusCode;

        private static IntPtr WebResponseHeaders(IntPtr handle) =>
            NewHandle<HttpResponseHeaders>(HandleRef<HttpResponseMessage>(handle).Headers);

        private static IntPtr WebResponseAsyncReader(IntPtr handle) =>
            // important: handle is not ref: rust throw away the ownership
            NewHandle<AsyncReader>(new AsyncReader(() =>
                OwnHandle<HttpResponseMessage>(handle).Content.ReadAsStreamAsync()));

        private static void WebHeadersGet(IntPtr handle, IntPtr name, IntPtr header)
        {
            var first = HandleRef<HttpResponseHeaders>(handle).GetValues((*(RsSlice*)name).AsString()).FirstOrDefault();
            (*(CsSlice*)header) = first == null ? default : CsSlice.Of(first);
        }

        // ReSharper disable InconsistentNaming
        public static readonly NoToVoid version_mismatch = VersionMismatch;
        public static readonly Ptr4ToBool display_dialog = DisplayDialog;
        public static readonly Ptr1ToVoid log_error = LogError;
        public static readonly Ptr2ToVoid guid_to_asset_path = GuidToAssetPath;
        public static readonly Ptr1ToVoid free_cs_memory = FreeCsMemory;
        public static readonly Ptr1ToBool verify_url = VerifyUrl;
        public static readonly Ptr1ToPtr web_client_new = WebClientNew;
        public static readonly Ptr2ToPtr web_request_new_get = WebRequestNewGet;

        public static readonly Ptr4ToVoid web_request_add_header = WebRequestAddHeader;

        // important: not ref: rust throw away the ownership
        public static readonly Ptr5ToVoid web_request_send = AsyncCallbacks.WebRequestSend;
        public static readonly Ptr1ToUInt web_response_status = WebResponseStatus;

        public static readonly Ptr1ToPtr web_response_headers = WebResponseHeaders;

        // important: handle is not ref: rust throw away the ownership
        public static readonly Ptr1ToPtr web_response_async_reader = WebResponseAsyncReader;

        // important: handle is not ref: rust throw away the ownership
        public static readonly Ptr5ToVoid web_response_bytes_async = AsyncCallbacks.WebResponseBytesAsync;
        public static readonly Ptr3ToVoid web_headers_get = WebHeadersGet;
        public static readonly Ptr5ToVoid web_async_reader_read = AsyncCallbacks.WebAsyncReaderRead;

        public static readonly Ptr5ToVoid async_unzip = AsyncCallbacks.AsyncUnzip;
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
                    unsafe
                    {
                        *(CsErr*)err = CsErr.Of(e);
                    }
                }
            });
            var awaiter = task.ConfigureAwait(false).GetAwaiter();
            if (awaiter.IsCompleted)
                Marshal.GetDelegateForFunctionPointer<Ptr1ToVoid>(callback)(context);
            else
                awaiter.OnCompleted(() =>
                    Marshal.GetDelegateForFunctionPointer<Ptr1ToVoid>(callback)(context));
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

        public static void WebResponseBytesAsync(IntPtr handle, IntPtr slice, IntPtr err, IntPtr context,
            IntPtr callback)
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

        public static void AsyncUnzip(IntPtr fileHandle, IntPtr destDir, IntPtr err, IntPtr context,
            IntPtr callback)
        {
            Async(err, context, callback, async () =>
            {
                await Task.Run(() =>
                {
                    using (var safeFileHandle = new SafeFileHandle(fileHandle, true))
                    using (var fileStream = new FileStream(safeFileHandle, FileAccess.Write))
                    using (var source = new ZipArchive(fileStream, ZipArchiveMode.Read, false, Encoding.UTF8))
                        unsafe
                        {
                            source.ExtractToDirectory((*(RsSlice*)destDir).AsString());
                        }
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

    struct NativeCsData
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
    }
    // ReSharper restore InconsistentNaming

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void NoToVoid();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void Ptr1ToVoid(IntPtr arg0);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void Ptr2ToVoid(IntPtr arg0, IntPtr arg1);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void Ptr3ToVoid(IntPtr arg0, IntPtr arg1, IntPtr arg2);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void Ptr4ToVoid(IntPtr arg0, IntPtr arg1, IntPtr arg2, IntPtr arg3);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void Ptr5ToVoid(IntPtr arg0, IntPtr arg1, IntPtr arg2, IntPtr arg3, IntPtr arg4);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool Ptr1ToBool(IntPtr arg0);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate bool Ptr4ToBool(IntPtr arg0, IntPtr arg1, IntPtr arg2, IntPtr arg3);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr Ptr1ToPtr(IntPtr arg0);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate IntPtr Ptr2ToPtr(IntPtr arg0, IntPtr arg1);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate uint Ptr1ToUInt(IntPtr handle);

    unsafe struct RustGUID
    {
        public fixed byte bytes[128 / 8];
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
            var handle = NewHandle(array, GCHandleType.Pinned);
            return new CsSlice(
                handle,
                GCHandle.FromIntPtr(handle).AddrOfPinnedObject() + offset * sizeof(T),
                (IntPtr)len);
        }

        public static CsSlice Of(string str) => Of(UTF8.GetBytes(str));
    }

    readonly struct RsSlice
    {
#pragma warning disable CS0649
        private readonly IntPtr ptr; //T *ptr;
        private readonly IntPtr len;
#pragma warning restore CS0649

        public unsafe string AsString()
        {
            if ((ulong)len >= int.MaxValue)
                throw new InvalidOperationException("str too big to be string");
            return UTF8.GetString((byte*)ptr, (int)len);
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
}
