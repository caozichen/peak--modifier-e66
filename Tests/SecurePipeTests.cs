using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using PeakWorkshop.Plugin;

internal static class SecurePipeTests
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, int objectType, uint information,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);

    public static List<(string Name, Func<Task> Body)> Create() => new()
    {
        ("Native pipe descriptor has one current-user allow ACE, a protected DACL and the correct token owner", VerifySecurityAsync),
        ("First-instance protection rejects a duplicate pipe without changing the existing endpoint", DuplicateAsync),
        ("IPC errors contain the failing stage, full stack and inner exception, with five-second rate limiting", DiagnosticsAsync)
    };

    private static string Name() => "PeakWorkshop.SecurityAudit." + Guid.NewGuid().ToString("N");
    private static void Assert(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    private static Task VerifySecurityAsync()
    {
        using var pipe = SecurePipeFactory.Create(Name());
        uint error = GetSecurityInfo(pipe.SafePipeHandle, 6, 5, out _, out _, out _, out _, out var descriptor);
        Assert(error == 0, "GetSecurityInfo error=" + error);
        try
        {
            byte[] bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var actual = new RawSecurityDescriptor(bytes, 0);
            using var identity = WindowsIdentity.GetCurrent();
            Assert(actual.Owner == identity.Owner, "Native pipe owner differs from desktop CurrentUserOnly's expected owner.");
            Assert(actual.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected), "Pipe DACL inherits unrelated permissions.");
            Assert(actual.DiscretionaryAcl?.Count == 1, "Pipe DACL has additional access entries.");
            var ace = actual.DiscretionaryAcl![0] as CommonAce;
            Assert(ace != null && ace.AceQualifier == AceQualifier.AccessAllowed && ace.SecurityIdentifier == identity.User,
                "Only this token's user should have an allow ACE.");
            Assert(SecurePipeFactory.CurrentUserSid() == identity.User!.Value, "Native TokenUser differs from desktop identity.");
        }
        finally { LocalFree(descriptor); }
        return Task.CompletedTask;
    }

    private static Task DuplicateAsync()
    {
        string name = Name();
        using var first = SecurePipeFactory.Create(name);
        try { using var second = SecurePipeFactory.Create(name); throw new Exception("Duplicate secure endpoint was accepted."); }
        catch (IOException error) { Assert(error.Message.Contains("CreateNamedPipeW"), "Unexpected failure stage."); }
        Assert(!first.SafePipeHandle.IsClosed && !first.SafePipeHandle.IsInvalid, "Existing endpoint was closed.");
        return Task.CompletedTask;
    }

    private static async Task DiagnosticsAsync()
    {
        string name = Name();
        using var occupied = SecurePipeFactory.Create(name);
        var logs = new List<string>();
        using var server = new PipeServer(name, (_, _) => throw new Exception("unused"), () => { }, text => { lock (logs) logs.Add(text); });
        await Task.Delay(450);
        server.Stop();
        await server.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        lock (logs)
        {
            Assert(logs.Count == 1, "Repeated creation errors were not rate limited.");
            string log = logs[0];
            Assert(log.Contains("create-secure-pipe") && log.Contains("System.IO.IOException") && log.Contains("System.ComponentModel.Win32Exception") &&
                log.Contains("SecurePipeFactory.Create") && log.Contains("PipeServer.RunAsync"), "IPC log lost stage, exception chain or stack.");
        }
    }
}
