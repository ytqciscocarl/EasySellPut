namespace Meili.Shared.Services;

/// <summary>Only web articles belong in the reader; never dispatch custom protocols to the OS.</summary>
public static class NewsReaderPolicy
{
    public static bool IsAllowed(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && string.IsNullOrEmpty(uri.UserInfo);
}
