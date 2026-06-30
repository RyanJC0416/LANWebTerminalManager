using System.Text;
namespace LANWebTerminalManager.Services;

using LANWebTerminalManager.Models;

public static class PythonHelper
{
    private const string ServerScriptFileName = ".lan-web-terminal-server.py";

    private const string NoCacheServerScript = """
        import http.server
        import socketserver
        import sys

        class NoCacheHandler(http.server.SimpleHTTPRequestHandler):
            def end_headers(self):
                self.send_header("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0")
                self.send_header("Pragma", "no-cache")
                self.send_header("Expires", "0")
                super().end_headers()

        class LanWebTerminalServer(socketserver.ThreadingTCPServer):
            # Windows 上 reuse 会导致多个进程同时绑定同一端口，浏览器会收到空响应。
            allow_reuse_address = sys.platform != "win32"
            daemon_threads = True

        port = int(sys.argv[1])
        host = sys.argv[2]
        with LanWebTerminalServer((host, port), NoCacheHandler) as httpd:
            httpd.serve_forever()
        """;

    public static (string Command, string[] PrefixArgs)? ResolvePython()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LWM_PYTHON")))
        {
            return (Environment.GetEnvironmentVariable("LWM_PYTHON")!, []);
        }

        // Windows 上优先使用真实 python.exe，避免 py 启动器立刻退出导致服务挂掉。
        foreach (var candidate in new[] { "python", "python3", "py" })
        {
            if (!CommandExists(candidate)) continue;

            if (candidate == "py")
            {
                var resolved = ResolvePyLauncherExecutable();
                if (resolved is not null) return (resolved, []);
                continue;
            }

            return (candidate, []);
        }

        return null;
    }

    public static int StartServer(WebEndpoint endpoint)
    {
        var python = ResolvePython() ?? throw new InvalidOperationException("未找到 Python。请安装 Python 3 后重试。");
        var scriptPath = EnsureServerScript(endpoint.RootPath);
        var args = python.PrefixArgs
            .Concat([scriptPath, endpoint.Port.ToString(), endpoint.Host])
            .ToArray();
        return ShellHelper.StartDetached(python.Command, args, endpoint.RootPath, EndpointPaths.LogFile(endpoint));
    }

    private static string EnsureServerScript(string rootPath)
    {
        var scriptPath = Path.Combine(rootPath, ServerScriptFileName);
        // 脚本有更新时强制覆盖，确保 Windows 修复能生效。
        File.WriteAllText(scriptPath, NoCacheServerScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return scriptPath;
    }

    private static string? ResolvePyLauncherExecutable()
    {
        var result = ShellHelper.Run("py", ["-3", "-c", "import sys; print(sys.executable)"]);
        if (!result.Ok) return null;

        var executable = result.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(executable) || !File.Exists(executable) ? null : executable;
    }

    private static bool CommandExists(string command)
    {
        var result = ShellHelper.Run("where.exe", [command], timeoutMs: 3000);
        return result.Ok && !string.IsNullOrWhiteSpace(result.Stdout);
    }
}
