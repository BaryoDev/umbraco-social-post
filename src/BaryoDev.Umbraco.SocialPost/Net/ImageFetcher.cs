using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.SocialPost.Net;

public sealed record FetchedImage(byte[] Bytes, string ContentType);

/// <summary>
/// Downloads the post image. Only https or http on the site's own host (or a listed media host), and the
/// connection is pinned to an address that passed the public-address check. No redirects, size capped.
/// </summary>
public sealed class ImageFetcher(HttpClient client, IOptions<SocialPostOptions> options)
{
    public const string UserAgent = "BaryoDev.Umbraco.SocialPost";

    public async Task<FetchedImage?> FetchAsync(string? imageUrl, string pageUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0 || !AllowedHosts(pageUrl).Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.Value.HttpTimeoutSeconds)));
        try
        {
            using HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            string? type = response.Content.Headers.ContentType?.MediaType;
            if (!response.IsSuccessStatusCode || type is null || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || type.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            byte[]? bytes = await BoundedRead.ReadAsync(response.Content, options.Value.MaxImageBytes, timeout.Token);
            return bytes is { Length: > 0 } ? new FetchedImage(bytes, type) : null;
        }
        catch (HttpRequestException)
        {
            // Includes a blocked address: the connect callback throws and the post goes out without an image.
            return null;
        }
        catch (Exception e) when (e is OperationCanceledException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private HashSet<string> AllowedHosts(string pageUrl)
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out Uri? page))
        {
            hosts.Add(page.Host);
        }

        if (Uri.TryCreate(options.Value.SiteBaseUrl, UriKind.Absolute, out Uri? site))
        {
            hosts.Add(site.Host);
        }

        foreach (string h in options.Value.ImageHosts)
        {
            hosts.Add(h.Trim());
        }

        return hosts;
    }

    /// <summary>Resolves the host, refuses non-public addresses, and connects to the address it checked.</summary>
    public static async ValueTask<Stream> ConnectPinned(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        IPAddress? chosen = addresses.Length > 0 && addresses.All(IsPublic) ? addresses[0] : null;
        if (chosen is null)
        {
            throw new HttpRequestException("The image host does not resolve to a public address.");
        }

        var socket = new Socket(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(chosen, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        byte[] b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            return !(b[0] == 0 || b[0] == 10 || b[0] >= 224
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && b[1] is 18 or 19));
        }

        // IPv6: global unicast 2000::/3 only, minus the ranges that tunnel to IPv4
        // (6to4 2002::/16, Teredo 2001::/32) and documentation 2001:db8::/32.
        // NAT64, IPv4-compatible, ULA, link-local and site-local all fall outside 2000::/3.
        if ((b[0] & 0xE0) != 0x20) return false;
        if (b[0] == 0x20 && b[1] == 0x02) return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;
        return true;
    }
}
