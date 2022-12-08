using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace AspNetCore.IgniteServer.Utils
{
    internal static class DnsUtils
    {
        internal static string GetLocalIpAddress()
        {
            return GetLocalIpAddress(Dns.GetHostName());
        }

        internal static string GetLocalIpAddress(Uri uri)
        {
            return GetLocalIpAddress(uri.Host);
        }

        private static string GetLocalIpAddress(string hostName)
        {
            IPHostEntry host = Dns.GetHostEntryAsync(hostName).GetAwaiter().GetResult();
            foreach (IPAddress? ip in host.AddressList.OrderBy(ip => ip.ToString()))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }

            throw new Exception("Local IP Address Not Found!");
        }
    }
}
