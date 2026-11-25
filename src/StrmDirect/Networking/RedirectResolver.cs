using System.Text.RegularExpressions;
using MediaBrowser.Common.Net;
using MediaBrowser.Model.Services;

namespace StrmDirect.Networking;

internal sealed partial class RedirectResolver(IHttpClient http)
{
    public static bool TryNormalize(string? value, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            return false;

        var candidate = UnsafeCharacters().Replace(value.Trim(), match => Uri.EscapeDataString(match.Value));
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Host))
            return false;

        // Avoid Uri.AbsoluteUri canonicalization of signed URL paths and query strings.
        url = candidate;
        return true;
    }

    public async Task<string> ResolveAsync(string original, IRequest request, bool follow)
    {
        if (!follow)
            return original;

        try
        {
            using var response = await http.GetResponse(new MediaBrowser.Common.Net.HttpRequestOptions
            {
                Url = original,
                UserAgent = request.UserAgent ?? string.Empty,
                EnableDefaultUserAgent = false,
                BufferContent = false,
                TimeoutMs = 3000,
                CancellationToken = request.CancellationToken,
                ThrowOnErrorResponse = false,
                LogRequest = false,
                LogResponse = false,
                LogErrors = false
            }).ConfigureAwait(false);

            if ((int)response.StatusCode is >= 200 and < 300 && TryNormalize(response.ResponseUrl, out var final))
                return final;
        }
        catch (Exception) when (!request.CancellationToken.IsCancellationRequested)
        {
            // A failed probe must not prevent the client from following the original link.
        }

        return original;
    }

    [GeneratedRegex("[^A-Za-z0-9\\-._~:/?#\\[\\]@!$&'()*+,;=%]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnsafeCharacters();
}
