#nullable disable
using System;
using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PeakWorkshop.Plugin
{
    // Unity's Mono WindowsIdentity.Owner/User throw NotImplementedException.
    // Build the same-user ACL from the real Windows process token instead.
    internal static class SecurePipeFactory
    {
        private const uint TokenQuery = 0x0008;
        private const uint PipeAccessDuplex = 0x00000003;
        private const uint FileFlagOverlapped = 0x40000000;
        private const uint FileFlagFirstPipeInstance = 0x00080000;
        private const uint PipeRejectRemoteClients = 0x00000008;

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
        }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int informationClass, IntPtr information, uint length, out uint needed);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, out uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maximumInstances,
            uint outputBufferSize, uint inputBufferSize, uint defaultTimeout, ref SecurityAttributes attributes);

        internal static string CurrentUserSid() => CurrentTokenSid(1);

        private static string CurrentTokenSid(int informationClass)
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) throw NativeError("OpenProcessToken");
            try
            {
                uint needed;
                if (GetTokenInformation(token, informationClass, IntPtr.Zero, 0, out needed) || Marshal.GetLastWin32Error() != 122 || needed < IntPtr.Size)
                    throw NativeError("GetTokenInformation(size)");
                IntPtr information = Marshal.AllocHGlobal(checked((int)needed));
                try
                {
                    if (!GetTokenInformation(token, informationClass, information, needed, out needed)) throw NativeError("GetTokenInformation(" + informationClass + ")");
                    IntPtr text;
                    if (!ConvertSidToStringSidW(Marshal.ReadIntPtr(information), out text)) throw NativeError("ConvertSidToStringSidW");
                    try { return Marshal.PtrToStringUni(text) ?? throw new IOException("Windows 用户 SID 为空。"); }
                    finally { LocalFree(text); }
                }
                finally { Marshal.FreeHGlobal(information); }
            }
            finally { CloseHandle(token); }
        }

        internal static NamedPipeServerStream Create(string name)
        {
            if (string.IsNullOrEmpty(name) || name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0)
                throw new ArgumentException("管道名称非法。", nameof(name));
            string sid = CurrentUserSid();
            string owner = CurrentTokenSid(4); // TOKEN_OWNER: match .NET CurrentUserOnly's owner check even when elevated.
            IntPtr descriptor;
            uint descriptorSize;
            // Protected DACL: exactly one allow ACE, for this process's user SID.
            // Explicit owner also supports the modern desktop's CurrentUserOnly check.
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("O:" + owner + "D:P(A;;GA;;;" + sid + ")", 1, out descriptor, out descriptorSize))
                throw NativeError("ConvertStringSecurityDescriptorToSecurityDescriptorW");
            SafePipeHandle handle = null;
            try
            {
                var attributes = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), SecurityDescriptor = descriptor, InheritHandle = false };
                handle = CreateNamedPipeW(@"\\.\pipe\" + name,
                    PipeAccessDuplex | FileFlagOverlapped | FileFlagFirstPipeInstance, PipeRejectRemoteClients,
                    1, 65536, 65536, 0, ref attributes);
                if (handle.IsInvalid) throw NativeError("CreateNamedPipeW");
                var stream = new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
                handle = null; // The stream now owns the SafePipeHandle.
                return stream;
            }
            finally
            {
                handle?.Dispose();
                LocalFree(descriptor);
            }
        }

        private static IOException NativeError(string operation)
        {
            int error = Marshal.GetLastWin32Error();
            return new IOException(operation + " 失败，Windows 错误=" + error, new Win32Exception(error));
        }
    }
}
