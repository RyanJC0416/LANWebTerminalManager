namespace LANWebTerminalManager.Services;

using LANWebTerminalManager.Models;

public static class EndpointPaths
{
    private static string SupportDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LANWebTerminalManager");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string PidFile(WebEndpoint endpoint)
    {
        var dir = Path.Combine(SupportDirectory, "pids");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{endpoint.Id:N}-{endpoint.Port}.pid");
    }

    public static string LogFile(WebEndpoint endpoint)
    {
        var preferred = Path.Combine(endpoint.RootPath, $".lan-web-terminal-{endpoint.Port}.log");
        if (CanWriteToDirectory(endpoint.RootPath))
        {
            return preferred;
        }

        var fallbackDir = Path.Combine(SupportDirectory, "logs");
        Directory.CreateDirectory(fallbackDir);
        return Path.Combine(fallbackDir, $"{endpoint.Id:N}-{endpoint.Port}.log");
    }

    public static string LegacyPidFile(WebEndpoint endpoint) =>
        Path.Combine(endpoint.RootPath, $".lan-web-terminal-{endpoint.Port}.pid");

    public static void TryWritePid(WebEndpoint endpoint, int pid)
    {
        var path = PidFile(endpoint);
        TryDeleteFile(path);
        File.WriteAllText(path, pid.ToString());
    }

    public static void TryDeletePid(WebEndpoint endpoint)
    {
        TryDeleteFile(PidFile(endpoint));
        TryDeleteFile(LegacyPidFile(endpoint));
    }

    private static bool CanWriteToDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return false;

        var probe = Path.Combine(directory, $".lwm-write-test-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }

            File.Delete(path);
        }
        catch
        {
            // ignore stale pid cleanup failures
        }
    }
}
