using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace GungeonOnline
{
    // LAN networking over plain UDP. The host listens on a port; a joiner sends to the host's IP.
    // UDP can lose or reorder packets, so "reliable" packets get a sequence number and are re-sent
    // until the other side acknowledges them, then handed over in order.
    public class LanLink : Transport
    {
        // Every LAN packet starts with "GO" so we ignore unrelated traffic on the port.
        public const byte Magic0 = (byte)'G';
        public const byte Magic1 = (byte)'O';
        private const float ResendInterval = 0.2f;

        public enum PacketKind : byte
        {
            Unreliable = 1,
            Reliable = 2,     // + ushort sequence number
            Ack = 3,          // + ushort sequence number being acknowledged
            Discover = 4,     // "any hosts out there?" (broadcast by LanDiscovery)
            DiscoverReply = 5 // host's name + port
        }

        private class Pending
        {
            public ushort Seq;
            public byte[] Packet;
            public float LastSent;
        }

        private readonly UdpClient udp;
        private readonly bool isHost;
        private readonly int port;
        private IPEndPoint peer;

        private ushort nextSendSeq;
        private ushort nextRecvSeq;
        private readonly List<Pending> unacked = new List<Pending>();
        private readonly Dictionary<ushort, byte[]> early = new Dictionary<ushort, byte[]>();

        private LanLink(UdpClient udp, bool isHost, int port, IPEndPoint peer)
        {
            this.udp = udp;
            this.isHost = isHost;
            this.port = port;
            this.peer = peer;
        }

        public static LanLink Host(int port)
        {
            UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Any, port));
            IgnoreConnectionResets(udp);
            Plugin.Log.LogInfo("Hosting LAN game on port " + port);
            return new LanLink(udp, true, port, null);
        }

        // Call RaiseReady after the session has hooked up its handlers.
        public static LanLink Join(IPEndPoint host)
        {
            UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            IgnoreConnectionResets(udp);
            Plugin.Log.LogInfo("Joining LAN game at " + host);
            return new LanLink(udp, false, host.Port, host);
        }

        public void Start()
        {
            if (!isHost)
                RaiseReady();
        }

        public override string Kind { get { return "LAN"; } }
        public override bool IsHost { get { return isHost; } }
        public override string Info
        {
            get { return isHost ? "Your LAN address: " + LocalAddress() + ":" + port : "Host: " + peer; }
        }

        public override void Poll()
        {
            try
            {
                while (udp.Available > 0)
                {
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = udp.Receive(ref from);
                    HandleDatagram(data, from);
                }
            }
            catch (SocketException e)
            {
                Plugin.Log.LogWarning("LAN receive error: " + e.SocketErrorCode);
            }

            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < unacked.Count; i++)
            {
                if (now - unacked[i].LastSent >= ResendInterval)
                {
                    unacked[i].LastSent = now;
                    SendRaw(unacked[i].Packet, peer);
                }
            }
        }

        public override void Send(byte[] data, bool reliable)
        {
            if (peer == null)
                return;
            if (!reliable)
            {
                SendRaw(Wrap(PacketKind.Unreliable, null, data), peer);
                return;
            }
            Pending p = new Pending();
            p.Seq = nextSendSeq++;
            p.Packet = Wrap(PacketKind.Reliable, p.Seq, data);
            p.LastSent = Time.realtimeSinceStartup;
            unacked.Add(p);
            SendRaw(p.Packet, peer);
        }

        public override void DropPeer()
        {
            // A joiner keeps the host's address so Close() still knows where it was.
            if (isHost)
                peer = null;
            nextSendSeq = 0;
            nextRecvSeq = 0;
            unacked.Clear();
            early.Clear();
        }

        public override void Close()
        {
            DropPeer();
            udp.Close();
        }

        private void HandleDatagram(byte[] data, IPEndPoint from)
        {
            if (data.Length < 3 || data[0] != Magic0 || data[1] != Magic1)
                return;
            PacketKind kind = (PacketKind)data[2];

            if (kind == PacketKind.Discover)
            {
                if (isHost)
                    SendRaw(BuildDiscoverReply(), from);
                return;
            }

            if (peer == null || !peer.Equals(from))
            {
                // The host takes the first player that sends it game data as its partner.
                if (!(isHost && peer == null && kind == PacketKind.Reliable))
                    return;
                peer = from;
                Plugin.Log.LogInfo("LAN partner connected from " + from);
            }

            switch (kind)
            {
                case PacketKind.Unreliable:
                    RaisePacket(data, 3, data.Length - 3);
                    break;

                case PacketKind.Ack:
                    if (data.Length < 5) return;
                    ushort acked = BitConverter.ToUInt16(data, 3);
                    unacked.RemoveAll(delegate (Pending p) { return p.Seq == acked; });
                    break;

                case PacketKind.Reliable:
                    if (data.Length < 5) return;
                    ushort seq = BitConverter.ToUInt16(data, 3);
                    SendRaw(Wrap(PacketKind.Ack, seq, null), from);
                    short ahead = (short)(seq - nextRecvSeq);
                    if (ahead < 0)
                        return; // already delivered; our ack was lost, so they re-sent
                    byte[] body = new byte[data.Length - 5];
                    Buffer.BlockCopy(data, 5, body, 0, body.Length);
                    early[seq] = body;
                    // Hand over everything that's now in order.
                    byte[] next;
                    while (early.TryGetValue(nextRecvSeq, out next))
                    {
                        early.Remove(nextRecvSeq);
                        nextRecvSeq++;
                        RaisePacket(next, 0, next.Length);
                    }
                    break;
            }
        }

        private byte[] BuildDiscoverReply()
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(stream))
            {
                w.Write(Magic0); w.Write(Magic1); w.Write((byte)PacketKind.DiscoverReply);
                w.Write(Protocol.Version);
                w.Write(Plugin.DisplayName());
                w.Write(port);
                w.Flush();
                return stream.ToArray();
            }
        }

        public static byte[] Wrap(PacketKind kind, ushort? seq, byte[] body)
        {
            int header = seq.HasValue ? 5 : 3;
            byte[] packet = new byte[header + (body != null ? body.Length : 0)];
            packet[0] = Magic0;
            packet[1] = Magic1;
            packet[2] = (byte)kind;
            if (seq.HasValue)
            {
                packet[3] = (byte)(seq.Value & 0xFF);
                packet[4] = (byte)(seq.Value >> 8);
            }
            if (body != null)
                Buffer.BlockCopy(body, 0, packet, header, body.Length);
            return packet;
        }

        private void SendRaw(byte[] packet, IPEndPoint to)
        {
            if (to == null)
                return;
            try
            {
                udp.Send(packet, packet.Length, to);
            }
            catch (SocketException e)
            {
                Plugin.Log.LogWarning("LAN send error: " + e.SocketErrorCode);
            }
        }

        // On Windows, a UDP send to a port nobody listens on makes the *next* receive throw
        // "connection reset". That would kill the host whenever a partner closes the game, so turn it off.
        public static void IgnoreConnectionResets(UdpClient udp)
        {
            const int SIO_UDP_CONNRESET = -1744830452;
            try
            {
                udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception)
            {
                // Not supported on this platform; harmless.
            }
        }

        // The address other PCs on the network can reach us at, e.g. 192.168.1.20.
        // Home networks use 192.168.x, 10.x or 172.16-31.x; other addresses are usually VPN adapters
        // (Radmin uses 26.x, Hamachi 25.x), so those are only a fallback.
        public static string LocalAddress()
        {
            string fallback = "127.0.0.1";
            try
            {
                foreach (IPAddress address in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                        continue;
                    byte[] b = address.GetAddressBytes();
                    bool homeNetwork = b[0] == 192 && b[1] == 168 || b[0] == 10 || b[0] == 172 && b[1] >= 16 && b[1] <= 31;
                    if (homeNetwork)
                        return address.ToString();
                    if (fallback == "127.0.0.1")
                        fallback = address.ToString();
                }
            }
            catch (Exception)
            {
            }
            return fallback;
        }
    }
}
