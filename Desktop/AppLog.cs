using System.IO;

namespace PeakWorkshop.Desktop;

internal static class AppLog
{
    private static readonly object Gate = new();
    public static string DirectoryPath => PortableEnvironment.LogsPath;
    public static string FilePath => Path.Combine(DirectoryPath, "desktop.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 2 * 1024 * 1024)
                    File.Move(FilePath, Path.Combine(DirectoryPath, "desktop.previous.log"), true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* Logging must not stop a running game test. */ }
    }
}
