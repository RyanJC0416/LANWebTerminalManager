using System.Text.Json.Serialization;

namespace LANWebTerminalManager.Models;

public sealed class WebEndpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string RootPath { get; set; } = "";
    public int Port { get; set; } = 8088;
    public string Host { get; set; } = "0.0.0.0";
    public string UrlPath { get; set; } = "/";
    public bool AutoOpen { get; set; }

    [JsonIgnore]
    public string HostPortLabel => $"{Host}:{Port}";
}
