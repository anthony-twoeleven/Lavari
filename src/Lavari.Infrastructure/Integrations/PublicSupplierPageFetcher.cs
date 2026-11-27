using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Lavari.Application.Abstractions;
using Lavari.Domain.Errors;

namespace Lavari.Infrastructure.Integrations;

internal sealed partial class PublicSupplierPageFetcher : ISupplierPageFetcher
{
    private const int MaxHtmlBytes = 1_000_000;
    private const int MaxTextCharacters = 16_000;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly HashSet<string> SensitiveQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "key", "apikey", "code"
    };
    private static readonly string[] SensitiveQueryFragments =
    [
        "token", "auth", "signature", "secret", "password", "session", "cookie", "credential"
    ];
    private static readonly HttpClient Client = CreateClient();

    [GeneratedRegex(@"(?is)<(script|style|noscript|svg|iframe|template|object|head)\b[^>]*>.*?</\1\s*>",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ActiveContentPattern();

    [GeneratedRegex(@"(?is)<img\b[^>]*?\bsrc\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ImageSourcePattern();

    [GeneratedRegex(@"(?s)<!--.*?-->|<[^>]{0,2048}>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex MarkupPattern();

    [GeneratedRegex(@"(?i)</?(?:address|article|aside|blockquote|br|div|dl|dt|dd|fieldset|figcaption|figure|footer|form|h[1-6]|header|hr|li|main|nav|ol|p|pre|section|table|tbody|td|tfoot|th|thead|tr|ul)\b[^>]*>",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex BlockElementPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex WhitespacePattern();

    public async Task<string> FetchTextAsync(Uri sourceUri, CancellationToken ct = default)
    {
        ValidatePublicPageUri(sourceUri);
        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        request.Headers.UserAgent.ParseAdd("LavariSupplierCapture/1.0");
        request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var response = await Client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode ||
                !(string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(mediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase)) ||
                response.Content.Headers.ContentLength > MaxHtmlBytes)
                throw FetchFailed();

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int count;
            while ((count = await stream.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (buffer.Length + count > MaxHtmlBytes)
                    throw FetchFailed();
                await buffer.WriteAsync(chunk.AsMemory(0, count), timeout.Token);
            }

            var html = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var text = ExtractProductText(html, sourceUri);
            if (string.IsNullOrWhiteSpace(text))
                throw FetchFailed();
            return text;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw FetchFailed();
        }
        catch (HttpRequestException)
        {
            throw FetchFailed();
        }
        catch (IOException)
        {
            throw FetchFailed();
        }
        catch (RegexMatchTimeoutException)
        {
            throw FetchFailed();
        }
    }

    internal static string ExtractProductText(string html, Uri? baseUri = null)
    {
        var imageUrls = ImageSourcePattern().Matches(html)
            .Select(match => match.Groups[1].Success ? match.Groups[1].Value :
                match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value)
            .Select(value => NormalizeImageReference(WebUtility.HtmlDecode(value), baseUri))
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToArray();

        var withoutActiveContent = ActiveContentPattern().Replace(html, " ");
        var withLineBreaks = BlockElementPattern().Replace(withoutActiveContent, "\n");
        var text = WebUtility.HtmlDecode(MarkupPattern().Replace(withLineBreaks, " "));
        text = string.Join(Environment.NewLine, text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => WhitespacePattern().Replace(line, " ").Trim())
            .Where(line => line.Length > 0));
        if (imageUrls.Length > 0)
            text = $"{text}\nImages: {string.Join(' ', imageUrls)}".Trim();
        if (text.Length <= MaxTextCharacters)
            return text;
        var truncated = text[..MaxTextCharacters];
        var lastSpace = truncated.LastIndexOfAny(new[] { ' ', '\n' });
        return lastSpace > 0 ? truncated[..lastSpace] : truncated;
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) |
                        ((uint)bytes[2] << 8) | bytes[3];
            return !InSubnet(value, 0x00000000, 8) &&
                   !InSubnet(value, 0x0A000000, 8) &&
                   !InSubnet(value, 0x64400000, 10) &&
                   !InSubnet(value, 0x7F000000, 8) &&
                   !InSubnet(value, 0xA9FE0000, 16) &&
                   !InSubnet(value, 0xAC100000, 12) &&
                   !InSubnet(value, 0xC0000000, 24) &&
                   !InSubnet(value, 0xC0000200, 24) &&
                   !InSubnet(value, 0xC0586300, 24) &&
                   !InSubnet(value, 0xC0A80000, 16) &&
                   !InSubnet(value, 0xC6120000, 15) &&
                   !InSubnet(value, 0xC6336400, 24) &&
                   !InSubnet(value, 0xCB007100, 24) &&
                   !InSubnet(value, 0xE0000000, 4);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
            address.ScopeId != 0 ||
            (bytes[0] & 0xE0) != 0x20)
            return false;

        return !(bytes[0] == 0x20 && bytes[1] == 0x01 &&
                 (bytes[2] == 0x00 && bytes[3] == 0x00 ||
                  bytes[2] == 0x00 && bytes[3] == 0x10 ||
                  bytes[2] == 0x00 && bytes[3] == 0x20 ||
                  bytes[2] == 0x0D && bytes[3] == 0xB8)) &&
               !(bytes[0] == 0x20 && bytes[1] == 0x02);
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = ConnectToPublicAddressAsync
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("Supplier host did not resolve exclusively to public addresses.");

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException)
            {
                socket.Dispose();
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("Could not connect to the supplier host.");
    }

    private static void ValidatePublicPageUri(Uri sourceUri)
    {
        if (!sourceUri.IsAbsoluteUri ||
            sourceUri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(sourceUri.UserInfo) ||
            !string.IsNullOrEmpty(sourceUri.Fragment) ||
            (sourceUri.Scheme == "http" && sourceUri.Port != 80) ||
            (sourceUri.Scheme == "https" && sourceUri.Port != 443) ||
            HasSensitiveQueryParameter(sourceUri.Query))
            throw new ValidationServiceException(
                "Only public HTTP(S) product pages without credentials or private query tokens can be fetched.",
                possibleCause: "Paste the selected page text if the supplier page requires sign-in or uses a protected link.");
    }

    private static bool IsSafeImageReference(string value)
    {
        if (value.Length > 2000 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            HasSensitiveQueryParameter(uri.Query))
            return false;
        return true;
    }

    private static string? NormalizeImageReference(string value, Uri? baseUri)
    {
        value = value.Trim();
        if (!Uri.TryCreate(baseUri, value, out var uri) || !uri.IsAbsoluteUri)
            return null;
        var absoluteUrl = uri.AbsoluteUri;
        return IsSafeImageReference(absoluteUrl) ? absoluteUrl : null;
    }

    private static bool HasSensitiveQueryParameter(string query)
    {
        foreach (var parameter in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = WebUtility.UrlDecode(parameter.Split('=', 2)[0]);
            if (name is null)
                return true;
            name = name.Replace("-", string.Empty, StringComparison.Ordinal)
                .Replace("_", string.Empty, StringComparison.Ordinal)
                .Replace(".", string.Empty, StringComparison.Ordinal);
            if (SensitiveQueryNames.Contains(name) ||
                SensitiveQueryFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static bool InSubnet(uint address, uint network, int prefixLength)
    {
        var mask = uint.MaxValue << (32 - prefixLength);
        return (address & mask) == network;
    }

    private static ExternalIntegrationServiceException FetchFailed() => new(
        "The supplier page could not be fetched or parsed.",
        possibleCause: "The site may block automated access or require sign-in. Paste its product text and try again.");
}
