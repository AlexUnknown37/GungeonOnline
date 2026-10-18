using System;

namespace GungeonOnline
{
    // A "link" carries packets between us and the other players. Steam and LAN each have one;
    // NetSession talks to whichever is active and doesn't care which it is.
    //
    // Every other player we talk to directly is a "peer", numbered by slot. A guest has one peer, the host
    // (slot 0). The host has up to MaxPlayers - 1 guests, slots 1, 2, 3; a newcomer gets the lowest free slot.
    public abstract class Transport
    {
        public const int Everyone = -1;

        // A packet arrived from a peer: (slot, data, offset, length).
        public Action<int, byte[], int, int> OnPacket;
        // Joiner only: we can now reach the host, so the session should say Hello.
        public Action OnReady;
        // A peer is gone (left, disconnected, connection failed): (slot, reason).
        public Action<int, string> OnLost;

        public abstract string Kind { get; }
        public abstract bool IsHost { get; }
        // One line for the window, e.g. the LAN address friends should type in.
        public abstract string Info { get; }

        public abstract void Poll();
        // To one peer, or to every peer with Everyone.
        public abstract void Send(int slot, byte[] data, bool reliable);
        // Forget one peer. A host keeps listening for new ones.
        public abstract void DropPeer(int slot);
        // Shut the link down completely.
        public abstract void Close();

        public void Send(byte[] data, bool reliable)
        {
            Send(Everyone, data, reliable);
        }

        protected void RaiseReady()
        {
            if (OnReady != null)
                OnReady();
        }

        protected void RaiseLost(int slot, string reason)
        {
            if (OnLost != null)
                OnLost(slot, reason);
        }

        protected void RaisePacket(int slot, byte[] data, int offset, int length)
        {
            if (OnPacket != null)
                OnPacket(slot, data, offset, length);
        }
    }
}
