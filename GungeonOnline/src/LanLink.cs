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
    // until the other side acknowledges them, then handed over in order. The host keeps that bookkeeping per
    // guest (up to MaxPlayers - 1, by slot); a guest has one peer, the host (slot 0).
    public class LanLink : Transport
    {
        // Every LAN packet starts with "GO" so we ignore unrelated traffic on the port.
        public const byte Magic0 = (byte)'G';
        public const byte Magic1 = (byte)'O';
        private const float ResendInterval = 0.2f;

        public enum PacketKind : byte
        {
            Unreliable = 1,
            Reliable = 2,     // + ushort sequence number + ushort epoch
            Ack = 3,          // + ushort sequence number being acknowledged + ushort epoch
            Discover = 4,     // "any hosts out there?" (broadcast by LanDiscovery)
            DiscoverReply = 5 // host's name + port
        }

        private class Pending
        {
            public ushort Seq;
            public byte[] Packet;
            public float LastSent;
        }

        // Each side numbers its reliable packets per "epoch", a random number picked when it starts talking to a
        // peer. When one side starts over (the host dropped a player who timed out, a player rejoined), the other
        // sees a new epoch and starts counting from there, instead of waiting forever for a number that will
        // never come (which left a dropped player half-connected).
        private static readonly System.Random epochs = new System.Random();

        private class Peer
        {
            public IPEndPoint EndPoint;
            public ushort NextSendSeq;
            public ushort NextRecvSeq;
            public readonly ushort SendEpoch = (ushort)epochs.Next(1, 65536);
            public int RecvEpoch = -1;
            public readonly List<Pending> Unacked = new List<Pending>();
            public readonly Dictionary<ushort, byte[]> Early = new Dictionary<ushort, byte[]>();
            public Peer(IPEndPoint ep) { EndPoint = ep; }
        }

        private readonly UdpClient udp;
        private readonly bool isHost;
        private readonly int port;
        private readonly IPEndPoint hostAddress;    // joiner: where the host is
        private readonly Peer[] peers = new Peer[Protocol.MaxPlayers]; // by slot

        private LanLink(UdpClient udp, bool isHost, int port, IPEndPoint host)
        {
            this.udp = udp;
            this.isHost = isHost;
            this.port = port;
            hostAddress = host;
            if (host != null)
                peers[0] = new Peer(host);
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
            get { return isHost ? "Your LAN address: " + LocalAddress() + ":" + port : "Host: " + hostAddress; }
        }

        private readonly byte[] receiveBuffer = new byte[65536];

        public override void Poll()
        {
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
                    HandleDatagram(data, (IPEndPoint)any);
                }
            }
            catch (SocketException e)
            {
                Plugin.Log.LogWarning("LAN receive error: " + e.SocketErrorCode);
            }

            float now = Time.realtimeSinceStartup;
            foreach (Peer peer in peers)
            {
                if (peer == null)
                    continue;
                for (int i = 0; i < peer.Unacked.Count; i++)
                {
                    if (now - peer.Unacked[i].LastSent >= ResendInterval)
                    {
                        peer.Unacked[i].LastSent = now;
                        SendRaw(peer.Unacked[i].Packet, peer.EndPoint);
                    }
                }
            }
        }

        public override void Send(int slot, byte[] data, bool reliable)
        {
            byte[] unreliablePacket = null;
            for (int i = 0; i < peers.Length; i++)
            {
                Peer peer = peers[i];
                if (peer == null || (slot != Everyone && slot != i))
                    continue;
                if (!reliable)
                {
                    if (unreliablePacket == null)
                        unreliablePacket = Wrap(PacketKind.Unreliable, null, data);
                    SendRaw(unreliablePacket, peer.EndPoint);
                    continue;
                }
                Pending p = new Pending();
                p.Seq = peer.NextSendSeq++;
                p.Packet = Wrap(PacketKind.Reliable, p.Seq, data, peer.SendEpoch);
                p.LastSent = Time.realtimeSinceStartup;
                peer.Unacked.Add(p);
                SendRaw(p.Packet, peer.EndPoint);
            }
        }

        public override void DropPeer(int slot)
        {
            if (slot < 0 || slot >= peers.Length)
                return;
            // A joiner keeps the host's address (fresh bookkeeping) so a later hello still knows where to go.
            peers[slot] = !isHost && slot == 0 && hostAddress != null ? new Peer(hostAddress) : null;
        }

        public override void Close()
        {
            for (int i = 0; i < peers.Length; i++)
                peers[i] = null;
            udp.Close();
        }

        private int SlotOf(IPEndPoint from)
        {
            for (int i = 0; i < peers.Length; i++)
            {
                if (peers[i] != null && peers[i].EndPoint.Equals(from))
                    return i;
            }
            return -1;
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

            int slot = SlotOf(from);
            if (slot < 0)
            {
                // The host takes players that send it game data into the lowest free slot.
                if (!isHost || kind != PacketKind.Reliable)
                    return;
                for (int i = 1; i < peers.Length && slot < 0; i++)
                {
                    if (peers[i] == null)
                        slot = i;
                }
                if (slot < 0)
                {
                    Plugin.Log.LogWarning("Game is full: ignoring LAN player at " + from);
                    return;
                }
                peers[slot] = new Peer(from);
                Plugin.Log.LogInfo("LAN player connected from " + from + " (slot " + slot + ")");
            }
            Peer peer = peers[slot];

            switch (kind)
            {
                case PacketKind.Unreliable:
                    RaisePacket(slot, data, 3, data.Length - 3);
                    break;

                case PacketKind.Ack:
                    if (data.Length < 7) return;
                    ushort acked = BitConverter.ToUInt16(data, 3);
                    if (BitConverter.ToUInt16(data, 5) != peer.SendEpoch)
                        return; // an ack for an older connection
                    peer.Unacked.RemoveAll(delegate (Pending p) { return p.Seq == acked; });
                    break;

                case PacketKind.Reliable:
                    if (data.Length < 7) return;
                    ushort seq = BitConverter.ToUInt16(data, 3);
                    ushort epoch = BitConverter.ToUInt16(data, 5);
                    SendRaw(Wrap(PacketKind.Ack, seq, null, epoch), from);
                    if (peer.RecvEpoch != epoch)
                    {
                        // They started over (or this is the first packet): count from here.
                        peer.RecvEpoch = epoch;
                        peer.NextRecvSeq = seq;
                        peer.Early.Clear();
                    }
                    short ahead = (short)(seq - peer.NextRecvSeq);
                    if (ahead < 0)
                        return; // already delivered; our ack was lost, so they re-sent
                    byte[] body = new byte[data.Length - 7];
                    Buffer.BlockCopy(data, 7, body, 0, body.Length);
                    peer.Early[seq] = body;
                    // Hand over everything that's now in order (stop if handling one dropped this peer).
                    byte[] next;
                    while (peers[slot] == peer && peer.Early.TryGetValue(peer.NextRecvSeq, out next))
                    {
                        peer.Early.Remove(peer.NextRecvSeq);
                        peer.NextRecvSeq++;
                        RaisePacket(slot, next, 0, next.Length);
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

        public static byte[] Wrap(PacketKind kind, ushort? seq, byte[] body, ushort epoch = 0)
        {
            int header = seq.HasValue ? 7 : 3;
            byte[] packet = new byte[header + (body != null ? body.Length : 0)];
            packet[0] = Magic0;
            packet[1] = Magic1;
            packet[2] = (byte)kind;
            if (seq.HasValue)
            {
                packet[3] = (byte)(seq.Value & 0xFF);
                packet[4] = (byte)(seq.Value >> 8);
                packet[5] = (byte)(epoch & 0xFF);
                packet[6] = (byte)(epoch >> 8);
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
