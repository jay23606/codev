using System.Net;

namespace Codev;

public static class OllamaEndpoint
{
    public static readonly Uri Default = new("http://127.0.0.1:11434/");

    public static bool TryParse(string? value, out Uri endpoint, out string error)
    {
        endpoint = Default;
        error = "";
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
        {
            error = "Enter an HTTP or HTTPS Ollama base URL without credentials, query, or fragment.";
            return false;
        }

        var path = parsed.AbsolutePath.TrimEnd('/');
        endpoint = new UriBuilder(parsed) { Path = path.Length == 0 ? "/" : path + "/", Query = "", Fragment = "" }.Uri;
        return true;
    }

    public static bool IsLoopback(Uri endpoint) =>
        IPAddress.TryParse(endpoint.Host, out var address) ? IPAddress.IsLoopback(address) : endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    public static Uri ApiUri(Uri endpoint, string apiPath) => new(endpoint, apiPath.TrimStart('/'));
}
