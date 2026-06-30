namespace LANWebTerminalManager.Services;

public static class StringUrlExtensions
{
    public static string TrimmedSlash(this string value)
    {
        var trimmed = value.Trim();
        while (trimmed.EndsWith('/'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }

    public static string UrlQueryEscaped(this string value) =>
        Uri.EscapeDataString(value);
}
