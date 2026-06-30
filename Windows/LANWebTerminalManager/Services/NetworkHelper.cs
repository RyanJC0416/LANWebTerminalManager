using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace LANWebTerminalManager.Services;

public static class NetworkHelper
{
    private static readonly Regex PrivateIPv4 = new(
        @"^(192\.168\.\d+\.\d+|10\.\d+\.\d+\.\d+|172\.(1[6-9]|2\d|3[0-1])\.\d+\.\d+)$",
        RegexOptions.Compiled);

    public static bool IsPrivateIPv4(string value) => PrivateIPv4.IsMatch(value);

    public static List<int> ListenerPids(int port)
    {
        var script = $"""
            $ErrorActionPreference='SilentlyContinue'
            Get-NetTCPConnection -LocalPort {port} -State Listen |
            Select-Object -ExpandProperty OwningProcess
            """;
        var result = ShellHelper.Run("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script], timeoutMs: 5000);
        if (result.Ok && !string.IsNullOrWhiteSpace(result.Stdout))
        {
            return result.Stdout
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => int.TryParse(line.Trim(), out var pid) ? pid : -1)
                .Where(pid => pid > 0)
                .Distinct()
                .OrderBy(pid => pid)
                .ToList();
        }

        var netstat = ShellHelper.Run("netstat.exe", ["-ano", "-p", "tcp"], timeoutMs: 5000);
        var pattern = new Regex($@"[:.]{port}\s+.*LISTENING\s+(\d+)", RegexOptions.IgnoreCase);
        return netstat.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
            {
                var match = pattern.Match(line);
                return match.Success && int.TryParse(match.Groups[1].Value, out var pid) ? pid : -1;
            })
            .Where(pid => pid > 0)
            .Distinct()
            .OrderBy(pid => pid)
            .ToList();
    }

    public static bool IsPortOpen(string host, int port)
    {
        if (ListenerPids(port).Count > 0) return true;
        var target = host == "0.0.0.0" ? "127.0.0.1" : host;
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(IPAddress.Parse(target), port);
            return task.Wait(1500) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static string? PrimaryLanIp()
    {
        var script = """
            $ip = Get-NetIPConfiguration |
            Where-Object { $_.IPv4DefaultGateway -and $_.IPv4Address } |
            ForEach-Object { $_.IPv4Address.IPAddress } |
            Where-Object { $_ -match '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[0-1])\.)' } |
            Select-Object -First 1
            Write-Output $ip
            """;
        var ip = ShellHelper.Run("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script], timeoutMs: 5000)
            .Stdout.Trim();
        return IsPrivateIPv4(ip) ? ip : null;
    }

    public static List<string> LanIps()
    {
        var primary = PrimaryLanIp();
        if (primary is not null) return [primary];

        var script = """
            Get-NetIPAddress -AddressFamily IPv4 |
            Select-Object -ExpandProperty IPAddress
            """;
        var result = ShellHelper.Run("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script], timeoutMs: 5000);
        return result.Stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(IsPrivateIPv4)
            .Distinct()
            .ToList();
    }
}
