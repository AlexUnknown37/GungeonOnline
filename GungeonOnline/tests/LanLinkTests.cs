// Behaviour tests for the LAN reliable layer, driven through real loopback sockets with a
// deterministic "network" in between (drop / hold chosen packets). Run: run-tests.ps1
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using GungeonOnline;

public static class LanLinkTests
{
    static int failures;
    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
        if (!ok) failures++;
    }

    static readonly MethodInfo handle = typeof(LanLink).GetMethod("HandleDatagram", BindingFlags.NonPublic | BindingFlags.Instance);
    static readonly ConstructorInfo ctor = typeof(LanLink).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)[0];

    // A receiver whose acks land on a socket we can read.
    class Rig
    {
        public UdpClient LinkSocket, Sender;
        public LanLink Link;
        public IPEndPoint SenderEp;
        public List<int> Got = new List<int>();
        public Rig()
        {
            Sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            SenderEp = new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)Sender.Client.LocalEndPoint).Port);
            LinkSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            Link = (LanLink)ctor.Invoke(new object[] { LinkSocket, false, SenderEp.Port, SenderEp });
            Link.OnPacket = delegate (int slot, byte[] data, int offset, int length)
            {
                Got.Add(data[offset] | (data[offset + 1] << 8));
            };
        }
        public void Deliver(ushort seq, ushort epoch)
        {
            byte[] body = { (byte)(seq & 0xFF), (byte)(seq >> 8) };
            handle.Invoke(Link, new object[] { LanLink.Wrap(LanLink.PacketKind.Reliable, seq, body, epoch), SenderEp });
        }
        public HashSet<int> DrainAcks()
        {
            HashSet<int> acks = new HashSet<int>();
            System.Threading.Thread.Sleep(50);
            while (Sender.Available > 0)
            {
                IPEndPoint any = new IPEndPoint(IPAddress.Any, 0);
                byte[] d = Sender.Receive(ref any);
                if (d.Length >= 7 && d[2] == (byte)LanLink.PacketKind.Ack)
                    acks.Add(d[3] | (d[4] << 8));
            }
            return acks;
        }
        public void Close() { Sender.Close(); LinkSocket.Close(); }
    }

    // One reliable packet is lost while the sender keeps going. Everything the receiver acknowledges must
    // eventually be delivered: an acknowledged packet is never re-sent, so "acked but thrown away" is lost for good
    // and the ordered stream stops at that number for the rest of the session.
    static void AckedPacketsAreNeverDiscarded()
    {
        Rig rig = new Rig();
        const ushort epoch = 7;
        const int total = 1100;
        rig.Deliver(0, epoch);
        // seq 1 is lost in transit; 2..total arrive.
        HashSet<int> acked = new HashSet<int>();
        for (int s = 2; s <= total; s++)
        {
            rig.Deliver((ushort)s, epoch);
            if (s % 100 == 0)
                acked.UnionWith(rig.DrainAcks());
        }
        acked.UnionWith(rig.DrainAcks());
        // The re-send of seq 1 arrives.
        rig.Deliver(1, epoch);
        acked.UnionWith(rig.DrainAcks());

        HashSet<int> delivered = new HashSet<int>(rig.Got);
        int ackedButLost = 0, firstLost = -1;
        foreach (int s in acked)
        {
            if (!delivered.Contains(s))
            {
                ackedButLost++;
                if (firstLost < 0 || s < firstLost) firstLost = s;
            }
        }
        Check(ackedButLost == 0, "acked packets are all delivered (acked-but-discarded: " + ackedButLost
            + (firstLost >= 0 ? ", first seq " + firstLost : "") + ", delivered " + rig.Got.Count + " of " + (total + 1) + ")");

        // The sender now re-sends whatever was not acked (in order), as LanLink.Poll does. Afterwards the stream
        // must be complete and in order.
        for (int s = 0; s <= total; s++)
        {
            if (!acked.Contains(s))
                rig.Deliver((ushort)s, epoch);
        }
        bool inOrder = rig.Got.Count == total + 1;
        for (int i = 0; inOrder && i < rig.Got.Count; i++)
            inOrder = rig.Got[i] == i;
        Check(inOrder, "stream is complete and in order after re-sends (delivered " + rig.Got.Count + " of " + (total + 1) + ")");
        rig.Close();
    }

    static void DuplicatesAndReorderDeliverOnce()
    {
        Rig rig = new Rig();
        ushort[] seqs = { 0, 2, 1, 1, 3, 2, 0, 5, 4 };
        foreach (ushort q in seqs) rig.Deliver(q, 3);
        Check(string.Join(",", rig.Got.ConvertAll(delegate (int x) { return x.ToString(); }).ToArray()) == "0,1,2,3,4,5",
            "duplicates and reordering deliver each packet once, in order");
        rig.Close();
    }

    static void BuiltInSelfTestStillPasses()
    {
        string result;
        Check(LanLink.SelfTest(out result), "LanLink.SelfTest (" + result + ")");
    }

    public static int Main()
    {
        BuiltInSelfTestStillPasses();
        DuplicatesAndReorderDeliverOnce();
        AckedPacketsAreNeverDiscarded();
        Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
        return failures;
    }
}
