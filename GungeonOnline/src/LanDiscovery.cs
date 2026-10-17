using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;

namespace GungeonOnline
{
    // Finds LAN hosts by shouting "any hosts?" to the whole local network (a UDP broadcast).
    // Every host running Gungeon Online answers with its name.
    public class LanDiscovery
    {
        public class FoundHost
        {
            public string Name;
            public IPEndPoint Address;
            public bool SameVersion;
        }

        public readonly List<FoundHost> Found = new List<FoundHost>();
        private UdpClient udp;

        public bool Searching { get { return udp != null; } }

        public void Search(int port)
        {
            Stop();
            Found.Clear();
            try
            {
                udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
                udp.EnableBroadcast = true;
                LanLink.IgnoreConnectionResets(udp);
                byte[] ask = LanLink.Wrap(LanLink.PacketKind.Discover, null, null);
                udp.Send(ask, ask.Length, new IPEndPoint(IPAddress.Broadcast, port));
                // Also ask this PC directly, for two copies of the game on one computer.
                udp.Send(ask, ask.Length, new IPEndPoint(IPAddress.Loopback, port));
            }
            catch (SocketException e)
            {
                Plugin.Log.LogWarning("LAN search failed: " + e.SocketErrorCode);
                Stop();
            }
        }

        private readonly byte[] receiveBuffer = new byte[65536];

        public void Poll()
        {
            if (udp == null)
                return;
            try
            {
                // UdpClient.Receive makes a new 64 KB buffer for every packet (a lot of garbage at dozens of packets
                // a second); receive into one reused buffer and copy only the packet's bytes.
                while (udp.Available > 0)
                {
                    EndPoint any = new IPEndPoint(IPAddress.Any, 0);
                    int n = udp.Client.ReceiveFrom(receiveBuffer, ref any);
                    byte[] data = new byte[n];
                    System.Buffer.BlockCopy(receiveBuffer, 0, data, 0, n);
                    HandleReply(data, (IPEndPoint)any);
                }
            }
            catch (SocketException)
            {
            }
        }

        public void Stop()
        {
            if (udp != null)
                udp.Close();
            udp = null;
        }

        private void HandleReply(byte[] data, IPEndPoint from)
        {
            if (data.Length < 3 || data[0] != LanLink.Magic0 || data[1] != LanLink.Magic1
                || data[2] != (byte)LanLink.PacketKind.DiscoverReply)
                return;
            try
            {
                using (BinaryReader r = new BinaryReader(new MemoryStream(data, 3, data.Length - 3)))
                {
                    FoundHost host = new FoundHost();
                    host.SameVersion = r.ReadInt32() == Protocol.Version;
                    host.Name = r.ReadString();
                    host.Address = new IPEndPoint(from.Address, r.ReadInt32());
                    // The same host can answer twice (broadcast + loopback); keep one.
                    foreach (FoundHost existing in Found)
                    {
                        if (existing.Address.Equals(host.Address))
                            return;
                    }
                    Found.Add(host);
                }
            }
            catch (Exception)
            {
                // Malformed reply; ignore.
            }
        }
    }
}
