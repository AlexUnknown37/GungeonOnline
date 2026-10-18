using System.Collections.Generic;
using Steamworks;

namespace GungeonOnline
{
    // Steam P2P networking. Steam handles NAT punch-through and relays, so nobody needs to port forward.
    // Players find each other through a SteamLobby. Guests send only to the host (the lobby owner); the host
    // talks to every guest and gives each one a slot (1, 2, 3) the first time it hears from them.
    public class SteamLink : Transport
    {
        public readonly SteamLobby Lobby;

        private readonly CSteamID[] peers = new CSteamID[Protocol.MaxPlayers]; // by slot; Nil = free
        private Callback<P2PSessionRequest_t> sessionRequest;
        private Callback<P2PSessionConnectFail_t> connectFail;
        private byte[] buffer = new byte[4096];

        // Steam callbacks must exist for the whole game, because a friend's invite can arrive at any time.
        public SteamLink(SteamLobby lobby)
        {
            Lobby = lobby;
            sessionRequest = Callback<P2PSessionRequest_t>.Create(OnSessionRequest);
            connectFail = Callback<P2PSessionConnectFail_t>.Create(OnSessionConnectFail);
            SteamNetworking.AllowP2PPacketRelay(true);
            Lobby.OnEntered += OnEnteredLobby;
            Lobby.OnMemberLeft += OnMemberLeft;
        }

        public override string Kind { get { return "Steam"; } }
        public override bool IsHost { get { return Lobby.IsHost; } }
        public override string Info { get { return Lobby.InLobby ? "Steam lobby " + Lobby.LobbyId.m_SteamID : "Not in a lobby"; } }

        public override void Poll()
        {
            uint size;
            while (SteamNetworking.IsP2PPacketAvailable(out size, Protocol.Channel))
            {
                if (size > buffer.Length)
                    buffer = new byte[size];

                uint read;
                CSteamID from;
                if (!SteamNetworking.ReadP2PPacket(buffer, size, out read, out from, Protocol.Channel))
                    break;

                int slot = SlotOf(from);
                if (slot < 0)
                {
                    // A host takes lobby members that talk to it into the lowest free slot.
                    if (!Lobby.IsHost || !Lobby.IsMember(from))
                        continue;
                    slot = FreeSlot();
                    if (slot < 0)
                    {
                        Plugin.Log.LogWarning("Game is full: ignoring " + SteamFriends.GetFriendPersonaName(from));
                        continue;
                    }
                    peers[slot] = from;
                }
                RaisePacket(slot, buffer, 0, (int)read);
            }
        }

        private int SlotOf(CSteamID user)
        {
            for (int i = 0; i < peers.Length; i++)
            {
                if (peers[i] == user && user != CSteamID.Nil)
                    return i;
            }
            return -1;
        }

        private int FreeSlot()
        {
            for (int i = 1; i < peers.Length; i++)
            {
                if (peers[i] == CSteamID.Nil)
                    return i;
            }
            return -1;
        }

        public override void Send(int slot, byte[] data, bool reliable)
        {
            EP2PSend mode = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliable;
            for (int i = 0; i < peers.Length; i++)
            {
                if (peers[i] != CSteamID.Nil && (slot == Everyone || slot == i))
                    SteamNetworking.SendP2PPacket(peers[i], data, (uint)data.Length, mode, Protocol.Channel);
            }
        }

        public override void DropPeer(int slot)
        {
            if (slot < 0 || slot >= peers.Length)
                return;
            if (peers[slot] != CSteamID.Nil)
                SteamNetworking.CloseP2PSessionWithUser(peers[slot]);
            peers[slot] = CSteamID.Nil;
        }

        public override void Close()
        {
            for (int i = 0; i < peers.Length; i++)
                DropPeer(i);
            Lobby.Leave();
        }

        private void OnEnteredLobby()
        {
            // Joining someone else's lobby: we talk to the owner (the host, slot 0).
            if (!Lobby.IsHost)
            {
                for (int i = 0; i < peers.Length; i++)
                    peers[i] = CSteamID.Nil;
                peers[0] = Lobby.Owner();
                RaiseReady();
            }
        }

        private void OnMemberLeft(CSteamID user)
        {
            int slot = SlotOf(user);
            if (slot >= 0)
                RaiseLost(slot, (slot == 0 ? "the host" : "a player") + " left the lobby");
        }

        private void OnSessionRequest(P2PSessionRequest_t request)
        {
            CSteamID remote = request.m_steamIDRemote;
            if (Lobby.IsMember(remote))
            {
                SteamNetworking.AcceptP2PSessionWithUser(remote);
                Plugin.Log.LogInfo("Accepted Steam connection from " + SteamFriends.GetFriendPersonaName(remote));
            }
            else
            {
                Plugin.Log.LogWarning("Ignored Steam connection from non-lobby user " + remote.m_SteamID);
            }
        }

        private void OnSessionConnectFail(P2PSessionConnectFail_t fail)
        {
            Plugin.Log.LogWarning("Steam connection to " + fail.m_steamIDRemote.m_SteamID + " failed, error " + fail.m_eP2PSessionError);
            int slot = SlotOf(fail.m_steamIDRemote);
            if (slot >= 0)
                RaiseLost(slot, "Steam connection failed");
        }
    }
}
