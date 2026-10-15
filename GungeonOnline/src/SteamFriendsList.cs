using System.Collections.Generic;
using Steamworks;
using UnityEngine;

namespace GungeonOnline
{
    // Steam friends who are playing Enter the Gungeon right now, for the Multiplayer menu.
    // - Steam tells us which game each friend is in (GetFriendGamePlayed) and, if they're in a joinable lobby,
    //   which one (m_steamIDLobby) — so a friend hosting Gungeon Online gets a Join button, no invite needed.
    // - Our own "rich presence" tags (set while online) tell friends' copies of the mod that we run
    //   Gungeon Online and which version; "connect" makes Steam's own "Join Game" work from the friends list.
    public static class SteamFriendsList
    {
        public const uint GungeonAppId = 311690;
        private const string ModKey = "gungeononline";   // rich presence: our mod version while online
        private const float RefreshEvery = 3f;

        public class Friend
        {
            public CSteamID Id;
            public string Name;
            public string ModVersion = "";   // empty: not running Gungeon Online (or not online in it)
            public CSteamID Lobby = CSteamID.Nil;
            public bool Hosting { get { return Lobby != CSteamID.Nil && Lobby.m_SteamID != 0; } }
        }

        public static readonly List<Friend> Playing = new List<Friend>();
        private static float nextRefreshAt;

        public static void Refresh(bool force)
        {
            if (!SteamManager.Initialized)
            {
                Playing.Clear();
                return;
            }
            if (!force && Time.realtimeSinceStartup < nextRefreshAt)
                return;
            nextRefreshAt = Time.realtimeSinceStartup + RefreshEvery;

            Playing.Clear();
            int count = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
            for (int i = 0; i < count; i++)
            {
                CSteamID id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                FriendGameInfo_t game;
                if (!SteamFriends.GetFriendGamePlayed(id, out game) || game.m_gameID.AppID().m_AppId != GungeonAppId)
                    continue;
                Friend f = new Friend();
                f.Id = id;
                f.Name = SteamFriends.GetFriendPersonaName(id);
                f.ModVersion = SteamFriends.GetFriendRichPresence(id, ModKey) ?? "";
                f.Lobby = game.m_steamIDLobby;
                Playing.Add(f);
            }
            // Hosts first, then people with the mod, then the rest; alphabetical within each.
            Playing.Sort(delegate (Friend a, Friend b)
            {
                int ra = a.Hosting ? 0 : a.ModVersion.Length > 0 ? 1 : 2;
                int rb = b.Hosting ? 0 : b.ModVersion.Length > 0 ? 1 : 2;
                return ra != rb ? ra.CompareTo(rb) : string.Compare(a.Name, b.Name);
            });
        }

        // Our own tags, kept in step with the session (called every frame; only changes when needed).
        private static string lastPresence = null;

        public static void UpdateOwnPresence(NetSession session)
        {
            if (!SteamManager.Initialized)
                return;
            bool online = session.Link != null;
            CSteamID lobby = session.Steam != null && session.Steam.Lobby.InLobby ? session.Steam.Lobby.LobbyId : CSteamID.Nil;
            string presence = online ? Plugin.ModVersion + "|" + lobby.m_SteamID : "";
            if (presence == lastPresence)
                return;
            lastPresence = presence;
            SteamFriends.SetRichPresence(ModKey, online ? Plugin.ModVersion : "");
            // Lets friends use Steam's own "Join Game" on us (the game then starts with +connect_lobby).
            SteamFriends.SetRichPresence("connect", lobby != CSteamID.Nil ? "+connect_lobby " + lobby.m_SteamID : "");
        }
    }
}
