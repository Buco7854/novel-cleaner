using System.Net;
using System.Net.Sockets;

namespace NovelCleaner.Server.Services;

/// <summary>
/// Builds a <see cref="SocketsHttpHandler"/> for outbound OPDS requests that
///  - refuses redirects (so attached <c>Authorization</c> headers can never be
///    leaked to a different host); and
///  - rejects connections to private / loopback / link-local addresses *after*
///    DNS resolution, defeating both literal-IP SSRF and DNS rebinding.
/// </summary>
internal static class OpdsHttpHandler
{
    public static SocketsHttpHandler Create(ILogger logger, bool allowPrivateNetworks = false) => new()
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = async (ctx, ct) =>
        {
            var host = ctx.DnsEndPoint.Host;
            var port = ctx.DnsEndPoint.Port;

            // Resolve once and pin the safe address we choose, so a malicious
            // DNS server can't return a different (internal) answer when
            // .NET re-resolves on the actual connect.
            var entries = await Dns.GetHostAddressesAsync(host, ct);
            IPAddress? safe = null;
            foreach (var addr in entries)
            {
                if (!allowPrivateNetworks && IsBlocked(addr))
                {
                    logger.LogWarning("OPDS connect blocked: host {Host} resolved to {Addr}", host, addr);
                    continue;
                }
                safe = addr;
                break;
            }
            if (safe is null)
                throw new IOException($"Refusing to connect to {host}: no public address resolved.");

            var socket = new Socket(safe.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(safe, port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    private static bool IsBlocked(IPAddress addr)
    {
        if (IPAddress.IsLoopback(addr)) return true;

        if (addr.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = addr.GetAddressBytes();
            // 10/8
            if (b[0] == 10) return true;
            // 172.16/12
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
            // 192.168/16
            if (b[0] == 192 && b[1] == 168) return true;
            // 169.254/16 link-local + cloud metadata (169.254.169.254)
            if (b[0] == 169 && b[1] == 254) return true;
            // 127/8 (already caught by IsLoopback, but redundant)
            if (b[0] == 127) return true;
            // 0.0.0.0/8
            if (b[0] == 0)  return true;
            // 100.64/10 carrier-grade NAT
            if (b[0] == 100 && (b[1] & 0xC0) == 64) return true;
        }
        else if (addr.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(addr)) return true;
            if (addr.IsIPv6LinkLocal || addr.IsIPv6SiteLocal) return true;
            // Unique local fc00::/7
            var b = addr.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;
            // Mapped/embedded IPv4 — rewrap & re-check
            if (addr.IsIPv4MappedToIPv6) return IsBlocked(addr.MapToIPv4());
        }
        return false;
    }
}
