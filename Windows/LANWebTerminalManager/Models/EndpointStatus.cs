namespace LANWebTerminalManager.Models;

public sealed class EndpointStatus
{
    public bool Running { get; set; }
    public List<int> Pids { get; set; } = [];
    public List<string> Urls { get; set; } = [];
    public string LocalUrl { get; set; } = "";
    public int PageCount { get; set; }
    public string IndexMtime { get; set; } = "--";
    public string LogTail { get; set; } = "";
    public string UpdatedAt { get; set; } = "--";
}
