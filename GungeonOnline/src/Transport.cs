using System;

namespace GungeonOnline
{
    // A "link" carries packets between us and one partner. Steam and LAN each have one;
    // NetSession talks to whichever is active and doesn't care which it is.
    public abstract class Transport
    {
        // A packet arrived from the partner: data[offset .. offset+length).
        public Action<byte[], int, int> OnPacket;
        // Joiner only: we can now reach the host, so the session should say Hello.
        public Action OnReady;
        // The partner is gone (left, disconnected, connection failed).
        public Action<string> OnLost;

        public abstract string Kind { get; }
        public abstract bool IsHost { get; }
        // One line for the window, e.g. the LAN address friends should type in.
        public abstract string Info { get; }

        public abstract void Poll();
        public abstract void Send(byte[] data, bool reliable);
        // Forget the current partner. A host keeps listening for a new one.
        public abstract void DropPeer();
        // Shut the link down completely.
        public abstract void Close();

        protected void RaiseReady()
        {
            if (OnReady != null)
                OnReady();
        }

        protected void RaiseLost(string reason)
        {
            if (OnLost != null)
                OnLost(reason);
        }

        protected void RaisePacket(byte[] data, int offset, int length)
        {
            if (OnPacket != null)
                OnPacket(data, offset, length);
        }
    }
}
