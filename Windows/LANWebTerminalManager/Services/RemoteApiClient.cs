using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LANWebTerminalManager.Services;

public static class RemoteApiClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<byte[]> RequestAsync(string url, string method, byte[]? body = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        using var response = await Http.SendAsync(request);
        var data = await response.Content.ReadAsByteArrayAsync();
        if (!response.IsSuccessStatusCode)
        {
            var message = TryReadError(data) ?? $"请求失败：{(int)response.StatusCode}";
            throw new InvalidOperationException(message);
        }

        return data;
    }

    public static byte[] Request(string url, string method, byte[]? body = null) =>
        RequestAsync(url, method, body).GetAwaiter().GetResult();

    public static string PostJson(string url, object payload) =>
        Encoding.UTF8.GetString(Request(url, "POST", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions)));

    public static string GetJson(string url) =>
        Encoding.UTF8.GetString(Request(url, "GET"));

    private static string? TryReadError(byte[] data)
    {
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(data, JsonOptions);
            return map?.GetValueOrDefault("error");
        }
        catch
        {
            return null;
        }
    }
}
