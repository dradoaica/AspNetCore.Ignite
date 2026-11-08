using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace AspNetCore.IgniteServer.Utils;

internal static class DnsUtils
{
    internal static string GetLocalIpAddress()
    {
        return GetLocalIpAddress(Dns.GetHostName());
    }

    private static string GetLocalIpAddress(string hostName)
    {
        var host = Dns.GetHostEntryAsync(hostName).GetAwaiter().GetResult();
        foreach (var ip in host.AddressList.OrderBy(ip => ip.ToString()))
            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return ip.ToString();

        throw new InvalidOperationException("Local IP Address Not Found!");
    }
}