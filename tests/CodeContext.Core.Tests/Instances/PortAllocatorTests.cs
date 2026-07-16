using System.Net;
using System.Net.Sockets;
using CodeContext.Core.Instances;
using Xunit;

namespace CodeContext.Core.Tests.Instances
{
    public class PortAllocatorTests
    {
        [Fact]
        public void IsPortFree_UnoccupiedPort_ReturnsTrue()
        {
            // Grab a free port from the OS, release it, then check it.
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            Assert.True(PortAllocator.IsPortFree(port));
        }

        [Fact]
        public void IsPortFree_OccupiedPort_ReturnsFalse()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            try
            {
                Assert.False(PortAllocator.IsPortFree(port));
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public void AllocatePort_SkipsOccupiedPorts()
        {
            // Occupy the first candidate; allocation must return a later one.
            var start = 27890;
            var listener = new TcpListener(IPAddress.Loopback, start);
            listener.Start();
            try
            {
                var allocated = PortAllocator.AllocatePort(start, start + 10);
                Assert.InRange(allocated, start + 1, start + 10);
            }
            finally
            {
                listener.Stop();
            }
        }

        [Fact]
        public void AllocatePort_NoFreePortInRange_Throws()
        {
            var start = 27950;
            var listener = new TcpListener(IPAddress.Loopback, start);
            listener.Start();
            try
            {
                Assert.Throws<InvalidOperationException>(() => PortAllocator.AllocatePort(start, start));
            }
            finally
            {
                listener.Stop();
            }
        }
    }
}
