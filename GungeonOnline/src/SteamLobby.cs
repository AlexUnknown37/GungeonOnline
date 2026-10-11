using System;
using Steamworks;

namespace GungeonOnline
{
    // A Steam lobby is the "room" players gather in. The host creates it, friends join
    // through a Steam invite, and the lobby tells everyone who else is in it.
    public class SteamLobby
    {
        public CSteamID LobbyId = CSteamID.Nil;
        public bool IsHost;

        public bool InLobby { get { return LobbyId != CSteamID.Nil; } }

        public Action OnEntered;
        public Action<CSteamID> OnMemberJoined;
        public Action<CSteamID> OnMemberLeft;

        private Callback<GameLobbyJoinRequested_t> joinRequested;
        private Callback<LobbyEnter_t> lobbyEnter;
        private Callback<LobbyChatUpdate_t> chatUpdate;
        private CallResult<LobbyCreated_t> lobbyCreated;

        public void Init()
        {
            joinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnJoinRequested);
            lobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            chatUpdate = Callback<LobbyChatUpdate_t>.Create(OnChatUpdate);
            lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
        }

        public void Host()
        {
            if (InLobby)
                return;
            Plugin.Log.LogInfo("Creating lobby...");
            SteamAPICall_t call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, Protocol.MaxPlayers);
            lobbyCreated.Set(call, OnLobbyCreated);
        }

        public void Join(CSteamID lobby)
        {
            if (InLobby)
                Leave();
            Plugin.Log.LogInfo("Joining lobby " + lobby.m_SteamID + "...");
            SteamMatchmaking.JoinLobby(lobby);
        }

        public void Leave()
        {
            if (!InLobby)
                return;
            SteamMatchmaking.LeaveLobby(LobbyId);
            LobbyId = CSteamID.Nil;
            IsHost = false;
            Plugin.Log.LogInfo("Left lobby.");
        }

        public void OpenInviteOverlay()
        {
            if (InLobby)
                SteamFriends.ActivateGameOverlayInviteDialog(LobbyId);
        }

        public bool IsMember(CSteamID user)
        {
            if (!InLobby)
                return false;
            int count = SteamMatchmaking.GetNumLobbyMembers(LobbyId);
            for (int i = 0; i < count; i++)
            {
                if (SteamMatchmaking.GetLobbyMemberByIndex(LobbyId, i) == user)
                    return true;
            }
            return false;
        }

        public CSteamID Owner()
        {
            return SteamMatchmaking.GetLobbyOwner(LobbyId);
        }

        private void OnLobbyCreated(LobbyCreated_t result, bool ioFailure)
        {
            if (ioFailure || result.m_eResult != EResult.k_EResultOK)
            {
                Plugin.Log.LogError("Could not create lobby: " + result.m_eResult);
                return;
            }
            CSteamID id = new CSteamID(result.m_ulSteamIDLobby);
            // Tag the lobby so joiners can check they run the same mod version.
            SteamMatchmaking.SetLobbyData(id, "mod", Plugin.Guid);
            SteamMatchmaking.SetLobbyData(id, "protocol", Protocol.Version.ToString());
            Plugin.Log.LogInfo("Lobby created: " + id.m_SteamID);
            // Steam also sends LobbyEnter_t to the creator, which finishes the setup.
        }

        // A friend accepted our invite while the game was already running.
        private void OnJoinRequested(GameLobbyJoinRequested_t request)
        {
            Join(request.m_steamIDLobby);
        }

        private void OnLobbyEnter(LobbyEnter_t entered)
        {
            if (entered.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
            {
                Plugin.Log.LogError("Could not join lobby, response " + entered.m_EChatRoomEnterResponse);
                return;
            }

            CSteamID id = new CSteamID(entered.m_ulSteamIDLobby);
            string protocol = SteamMatchmaking.GetLobbyData(id, "protocol");
            bool weOwnIt = SteamMatchmaking.GetLobbyOwner(id) == SteamUser.GetSteamID();
            // The host may not have tagged the lobby yet when its own LobbyEnter_t arrives, so only joiners check.
            if (!weOwnIt && protocol != Protocol.Version.ToString())
            {
                Plugin.Log.LogError("Host runs a different Gungeon Online version (protocol " + protocol + ", ours " + Protocol.Version + "). Leaving.");
                SteamMatchmaking.LeaveLobby(id);
                return;
            }

            LobbyId = id;
            IsHost = weOwnIt;
            Plugin.Log.LogInfo((IsHost ? "Hosting" : "Joined") + " lobby " + id.m_SteamID);
            if (OnEntered != null)
                OnEntered();
        }

        private void OnChatUpdate(LobbyChatUpdate_t update)
        {
            if (update.m_ulSteamIDLobby != LobbyId.m_SteamID)
                return;
            CSteamID user = new CSteamID(update.m_ulSteamIDUserChanged);
            bool entered = (update.m_rgfChatMemberStateChange & (uint)EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0;
            if (entered)
            {
                if (OnMemberJoined != null)
                    OnMemberJoined(user);
            }
            else if (OnMemberLeft != null)
            {
                OnMemberLeft(user);
            }
        }
    }
}
