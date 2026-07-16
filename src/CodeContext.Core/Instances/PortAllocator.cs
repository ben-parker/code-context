using System.Net;
using System.Net.Sockets;

namespace CodeContext.Core.Instances
{
    public static class PortAllocator
    {
        public const int DefaultStartPort = 7890;
        public const int DefaultEndPort = 7990;

        /// <summary>
        /// Returns the first free port in [startPort, endPort]. A linear scan from a stable base
        /// keeps ports human-guessable; the caller must still tolerate the small bind race by
        /// retrying on bind failure.
        /// </summary>
        public static int AllocatePort(int startPort = DefaultStartPort, int endPort = DefaultEndPort)
        {
            for (var port = startPort; port <= endPort; port++)
            {
                if (IsPortFree(port)) return port;
            }
            throw new InvalidOperationException($"No free port found in range {startPort}-{endPort}.");
        }

        public static bool IsPortFree(int port)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }
    }
}
