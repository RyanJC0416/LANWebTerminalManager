using System.Text.Json.Serialization;

namespace LANWebTerminalManager.Models;

public sealed class RemoteTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string ServerURL { get; set; } = "";
    public string Token { get; set; } = "";
    public string Platform { get; set; } = "";

    [JsonIgnore]
    public string DisplayHost
    {
        get
        {
            if (Uri.TryCreate(ServerURL, UriKind.Absolute, out var uri)) return uri.Host;
            return ServerURL;
        }
    }
}

public sealed class ReceiverSettings
{
    public string RootPath { get; set; } = DefaultRootPath();
    public string Token { get; set; } = "lwm-server";
    public int Port { get; set; } = 4177;

    public static ReceiverSettings CreateDefault() => new()
    {
        RootPath = DefaultRootPath(),
        Token = "lwm-server",
        Port = 4177
    };

    private static string DefaultRootPath()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(documents, "LWM Server");
    }
}

public sealed class SiteFilePayload
{
    public string Path { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class ReceiverEndpointPayload
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public string Host { get; set; } = "0.0.0.0";
    public string UrlPath { get; set; } = "/";
}

public sealed class ReceiverSyncPayload
{
    public string Token { get; set; } = "";
    public ReceiverEndpointPayload Endpoint { get; set; } = new();
    public List<SiteFilePayload> Files { get; set; } = [];
}

public sealed class ReceiverCommandPayload
{
    public string Token { get; set; } = "";
    public string? Command { get; set; }
}

public sealed class ReceiverSettingsPayload
{
    public string Platform { get; set; } = "win32";
    public int ProtocolVersion { get; set; } = 1;
    public string? ReceiverRootPath { get; set; }
}

public sealed class ReceiverEndpointResponse
{
    public WebEndpoint? Endpoint { get; set; }
    public EndpointStatusDto? Status { get; set; }
}

public sealed class RemoteCommandResult
{
    public bool Ok { get; set; }
    public int Status { get; set; }
    public string Stdout { get; set; } = "";
    public string Stderr { get; set; } = "";
    public string Output { get; set; } = "";
}

public sealed class EndpointStatusDto
{
    public bool Running { get; set; }
    public List<int> Pids { get; set; } = [];
    public List<string> Urls { get; set; } = [];
    public int PageCount { get; set; }
    public string IndexMtime { get; set; } = "--";
    public string LogTail { get; set; } = "";
    public string UpdatedAt { get; set; } = "--";

    public EndpointStatus ToStatus(string localUrl)
    {
        return new EndpointStatus
        {
            Running = Running,
            Pids = Pids,
            Urls = Urls,
            LocalUrl = localUrl,
            PageCount = PageCount,
            IndexMtime = IndexMtime,
            LogTail = LogTail,
            UpdatedAt = UpdatedAt
        };
    }

    public static EndpointStatusDto FromStatus(EndpointStatus status)
    {
        return new EndpointStatusDto
        {
            Running = status.Running,
            Pids = status.Pids,
            Urls = status.Urls,
            PageCount = status.PageCount,
            IndexMtime = status.IndexMtime,
            LogTail = status.LogTail,
            UpdatedAt = status.UpdatedAt
        };
    }
}
