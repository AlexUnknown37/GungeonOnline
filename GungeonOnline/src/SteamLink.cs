using Steamworks;

namespace GungeonOnline
{
    // Steam P2P networking. Steam handles NAT punch-through and relays, so nobody needs to port forward.
    // Players find each other through a SteamLobby; packets go directly between the two Steam users.
    public class SteamLink : Transport
    {
        public readonly SteamLobby Lobby;

        private CSteamID peer = CSteamID.Nil;
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

                if (from != peer)
                {
                    // A host takes the first lobby member that talks to it as its partner.
                    if (!(Lobby.IsHost && peer == CSteamID.Nil && Lobby.IsMember(from)))
                        continue;
                    peer = from;
                }
                RaisePacket(buffer, 0, (int)read);
            }
        }

        public override void Send(byte[] data, bool reliable)
        {
            if (peer == CSteamID.Nil)
                return;
            EP2PSend mode = reliable ? EP2PSend.k_EP2PSendReliable : EP2PSend.k_EP2PSendUnreliable;
            SteamNetworking.SendP2PPacket(peer, data, (uint)data.Length, mode, Protocol.Channel);
        }

        public override void DropPeer()
        {
            if (peer != CSteamID.Nil)
                SteamNetworking.CloseP2PSessionWithUser(peer);
            peer = CSteamID.Nil;
        }

        public override void Close()
        {
            DropPeer();
            Lobby.Leave();
        }

        private void OnEnteredLobby()
        {
            // Joining someone else's lobby: the owner is our partner.
            if (!Lobby.IsHost)
            {
                peer = Lobby.Owner();
                RaiseReady();
            }
        }

        private void OnMemberLeft(CSteamID user)
        {
            if (user == peer)
                RaiseLost("partner left the lobby");
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
            if (fail.m_steamIDRemote == peer)
                RaiseLost("Steam connection failed");
        }
    }
}
