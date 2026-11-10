using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PeakWorkshop.Protocol
{
    internal static class PipeIdentity
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetNamedPipeClientComputerNameW(SafePipeHandle pipe, StringBuilder name, uint length);

        internal static void RequireServerProcess(NamedPipeClientStream pipe, int expectedPid)
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint actual) || actual != (uint)expectedPid)
                throw new IOException("管道服务器不是当前游戏进程，已拒绝连接。");
        }

        internal static void RequireLocalClient(NamedPipeServerStream pipe)
        {
            var name = new StringBuilder(256);
            bool success = GetNamedPipeClientComputerNameW(pipe.SafePipeHandle, name, 256);
            int error = Marshal.GetLastWin32Error();
            // Windows reports ERROR_PIPE_LOCAL (229) for a local pipe instead of a computer name.
            if (!success && error == 229) return;
            string computer = name.ToString().TrimStart('\\');
            if (!success || !string.Equals(computer, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                throw new IOException("仅接受本机控制窗口。系统错误=" + error);
        }
    }
}
