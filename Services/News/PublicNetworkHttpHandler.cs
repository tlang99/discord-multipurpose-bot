using System.Net;
using System.Net.Sockets;

namespace ModBot.Services.News;

/// <summary>Thrown when a feed URL (or a redirect it leads to) resolves to a private, loopback or otherwise
/// non-public address.</summary>
public class BlockedAddressException(string host) : Exception($"'{host}' resolves to a private or reserved network address.")
{
    public string Host { get; } = host;
}

/// <summary>
/// HTTP handler for fetching user-supplied URLs (e.g. <c>/news follow-feed</c>) that refuses to connect to anything
/// other than the public internet, so a feed URL can't be used to reach the bot host's local network, loopback
/// services or cloud metadata endpoints.
/// </summary>
/// <remarks>
/// The check runs in <see cref="SocketsHttpHandler.ConnectCallback"/>, i.e. on the IP actually being connected to,
/// so it also covers redirects (every hop opens its own connection) and DNS rebinding (the name is resolved once,
/// here, and that same address is used for the connection).
/// </remarks>
public static class PublicNetworkHttpHandler
{
    private const int MaxRedirects = 3;

    public static SocketsHttpHandler Create() => new()
    {
        // A proxy would make the callback see the proxy's address instead of the real destination.
        UseProxy = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = MaxRedirects,
        ConnectCallback = ConnectToPublicAddressAsync,
    };

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);

        // Reject if *any* address is non-public, rather than picking a public one - a host that mixes the two is
        // suspicious, and this keeps the behaviour predictable.
        if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
        {
            throw new BlockedAddressException(host);
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address),
            _ => false,
        };
    }

    private static bool IsPublicIPv4(byte[] b) => !(
        b[0] == 0 ||                                         // 0.0.0.0/8 "this network"
        b[0] == 10 ||                                        // 10.0.0.0/8 private
        b[0] == 127 ||                                       // 127.0.0.0/8 loopback
        (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||        // 100.64.0.0/10 carrier-grade NAT
        (b[0] == 169 && b[1] == 254) ||                      // 169.254.0.0/16 link-local / cloud metadata
        (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||         // 172.16.0.0/12 private
        (b[0] == 192 && b[1] == 0 && b[2] == 0) ||           // 192.0.0.0/24 IETF protocol assignments
        (b[0] == 192 && b[1] == 0 && b[2] == 2) ||           // 192.0.2.0/24 documentation
        (b[0] == 192 && b[1] == 168) ||                      // 192.168.0.0/16 private
        (b[0] == 198 && (b[1] == 18 || b[1] == 19)) ||       // 198.18.0.0/15 benchmarking
        (b[0] == 198 && b[1] == 51 && b[2] == 100) ||        // 198.51.100.0/24 documentation
        (b[0] == 203 && b[1] == 0 && b[2] == 113) ||         // 203.0.113.0/24 documentation
        b[0] >= 224);                                        // multicast, reserved, broadcast

    private static bool IsPublicIPv6(IPAddress address)
    {
        // Teredo tunnels to an obfuscated IPv4 address that can't be meaningfully checked, so it's refused outright.
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6Any) ||
            address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
            address.IsIPv6UniqueLocal || address.IsIPv6Teredo)
        {
            return false;
        }

        var b = address.GetAddressBytes();

        // 64:ff9b::/96 NAT64 and 2002::/16 6to4 embed an IPv4 address - judge them by that address.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b.Take(12).Skip(4).All(x => x == 0))
        {
            return IsPublicIPv4(b[12..16]);
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsPublicIPv4(b[2..6]);
        }

        // 2001:db8::/32 documentation; everything else outside 2000::/3 isn't globally routable unicast.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8)
        {
            return false;
        }

        return (b[0] & 0xE0) == 0x20;
    }
}
