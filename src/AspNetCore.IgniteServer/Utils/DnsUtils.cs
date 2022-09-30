using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace AspNetCore.IgniteServer.Utils
{
    internal sealed class DnsUtils
    {
        public static string GetLocalIPAddress()
        {
            return GetLocalIPAddress(Dns.GetHostName());
        }

        public static string GetLocalIPAddress(Uri uri)
        {
            return GetLocalIPAddress(uri.Host);
        }

        public static string GetLocalIPAddress(string hostName)
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
