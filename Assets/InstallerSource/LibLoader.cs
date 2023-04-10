using System;
using System.Runtime.InteropServices;

namespace Anatawa12.VpmPackageAutoInstaller.LibLoader
{
    abstract class LibLoader
    {
        private LibLoader() {}
        protected internal abstract IntPtr OpenLibrary(string path);
        protected internal abstract IntPtr GetAddress(IntPtr library, string func);
        protected internal abstract void CloseLibrary(IntPtr library);

        private static LibLoader _loader = DetectOSAndLoader();

        private static LibLoader DetectOSAndLoader()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return new WindowsLibLoader();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                return new MacOSLibLoader();
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return new LinuxLibLoader();
            return null;
        }

        public static NativeLibrary LoadLibrary(string path) => new NativeLibrary(_loader, path);

        // ReSharper disable InconsistentNaming
        // ReSharper disable IdentifierTypo
        class MacOSLibLoader : LibLoader
        {
            private const int RTLD_NOW = 0x2;

            [DllImport("__Internal")]
            private static extern IntPtr dlopen(string filename, int flag);

            [DllImport("__Internal")]
            private static extern IntPtr dlsym(IntPtr handle, string name);

            [DllImport("__Internal")]
            private static extern int dlclose(IntPtr handle);

            protected internal override IntPtr OpenLibrary(string path) => dlopen(filename: path, RTLD_NOW);
            protected internal override IntPtr GetAddress(IntPtr library, string func) => dlsym(library, func);
            protected internal override void CloseLibrary(IntPtr library) => dlclose(library);
        }

        // TODO: test
        class LinuxLibLoader : LibLoader
        {
            private const int RTLD_NOW = 0x2;

            [DllImport("dl")]
            private static extern IntPtr dlopen(string filename, int flag);

            [DllImport("dl")]
            private static extern IntPtr dlsym(IntPtr handle, string name);

            [DllImport("dl")]
            private static extern int dlclose(IntPtr handle);

            protected internal override IntPtr OpenLibrary(string path) => dlopen(filename: path, RTLD_NOW);
            protected internal override IntPtr GetAddress(IntPtr library, string func) => dlsym(library, func);
            protected internal override void CloseLibrary(IntPtr library) => dlclose(library);
        }

        // TODO: test
        class WindowsLibLoader : LibLoader
        {
            [DllImport("kernel32")]
            private static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string filename);

            [DllImport("kernel32")]
            private static extern IntPtr GetProcAddress(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name);

            [DllImport("kernel32")]
            private static extern bool FreeLibrary(IntPtr handle);

            protected internal override IntPtr OpenLibrary(string path) => LoadLibraryW(filename: path);
            protected internal override IntPtr GetAddress(IntPtr library, string func) => GetProcAddress(library, func);
            protected internal override void CloseLibrary(IntPtr library) => FreeLibrary(library);
        }
        // ReSharper restore IdentifierTypo
        // ReSharper restore InconsistentNaming
    }

    sealed class NativeLibrary : IDisposable
    {
        private IntPtr _handle;
        private readonly LibLoader _loader;

        internal NativeLibrary(LibLoader loader, string path)
        {
            if (loader == null) throw new InvalidOperationException("unsupported platform");
            _loader = loader;
            _handle = loader.OpenLibrary(path);
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("library cannot be loaded");
        }

        public IntPtr GetAddress(string func)
        {
            if (_handle == IntPtr.Zero) throw new ObjectDisposedException("disposed");
            return _loader.GetAddress(_handle, func);
        }

        ~NativeLibrary()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                _loader.CloseLibrary(_handle);
                _handle = IntPtr.Zero;
            }
            GC.SuppressFinalize(this);
        }
    }
}
