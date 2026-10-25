using System.Net;
using System.Net.Sockets;

namespace OpsBridge.Api.Endpoints;

public static class ConnectorPingNetworkPolicy
{
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            // Resolve and connect to the SAME vetted address. A separate DNS
            // preflight followed by HttpClient's own resolution permits rebinding.
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            if (addresses.Length == 0 || addresses.Any(address => !IsPublicAddress(address)))
            {
                throw new HttpRequestException("Connector destination is not a public address.");
            }

            var address = addresses[0];
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var first = bytes[0];
            var second = bytes[1];
            var third = bytes[2];
            return first is > 0 and < 224
                && first != 10
                && first != 127
                && !(first == 100 && second is >= 64 and <= 127)
                && !(first == 169 && second == 254)
                && !(first == 172 && second is >= 16 and <= 31)
                && !(first == 192 && (second == 0 || second == 168))
                && !(first == 198 && ((second == 18 || second == 19) || (second == 51 && third == 100)))
                && !(first == 203 && second == 0 && third == 113);
        }

        // Only global IPv6 unicast (2000::/3) is reachable.
        return address.AddressFamily == AddressFamily.InterNetworkV6
            && bytes[0] is >= 0x20 and <= 0x3f;
    }
}
