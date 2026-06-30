using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace LANWebTerminalManager.Services;

public readonly record struct ShellResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

public static class ShellHelper
{
    private static readonly ConcurrentDictionary<int, Process> RunningProcesses = new();

    public static ShellResult Run(string executable, IEnumerable<string>? args = null, string? cwd = null, int timeoutMs = 10000)
    {
        using var process = CreateProcess(executable, args, cwd);
        try
        {
            process.Start();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                return new ShellResult(-1, "", "命令执行超时");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            return new ShellResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return new ShellResult(-1, "", ex.Message);
        }
    }

    public static ShellResult RunCommand(string command, string? cwd = null, int timeoutMs = 60000)
    {
        return Run("cmd.exe", ["/d", "/s", "/c", command], cwd, timeoutMs);
    }

    public static int StartDetached(string executable, IEnumerable<string> args, string cwd, string logPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllText(logPath, "", Encoding.UTF8);

        var logStream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(logStream, Encoding.UTF8) { AutoFlush = true };

        var process = CreateProcess(executable, args, cwd);
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) writer.WriteLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) writer.WriteLine(e.Data);
        };
        process.Exited += (_, _) =>
        {
            try { writer.Dispose(); } catch { }
            RunningProcesses.TryRemove(process.Id, out _);
            process.Dispose();
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        RunningProcesses[process.Id] = process;
        return process.Id;
    }

    private static Process CreateProcess(string executable, IEnumerable<string>? args, string? cwd)
    {
        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = BuildArgumentString(args),
                WorkingDirectory = cwd ?? Environment.CurrentDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
    }

    private static string BuildArgumentString(IEnumerable<string>? args)
    {
        return args is null ? "" : string.Join(" ", args.Select(Quote));
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"')) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
