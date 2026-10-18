using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace GungeonOnline
{
    // A player's extra companions (Ser Junkan, the Baby Good Mimic, the Pig, a second pet...). The player's first
    // companion travels with their normal updates (it's the one that can be petted); the rest are sent here ~10 times
    // a second: which companion, its animation frame, facing and position. The other games draw copies. Only pictures:
    // what companions do happens in their owner's game (their bullets are copied like the player's, see BulletSync).
    public static class CompanionSync
    {
        private const int MaxExtras = 3;
        private const float SendInterval = 0.1f;

        private static NetSession session;
        private static float nextSendAt;
        private static int lastSentCount;

        private class Entry
        {
            public string Guid = "";
            public string Clip = "";
            public int Frame;
            public bool FlipX;
            public Vector2 Target;
            public Vector2 Shown;
            public bool HasShown;
        }

        private static readonly Entry[][] entries = NewEntries();
        private static readonly int[] counts = new int[Protocol.MaxPlayers];
        private static readonly float[] receivedAt = new float[Protocol.MaxPlayers];
        private static readonly int[] scenes = new int[Protocol.MaxPlayers];
        private static readonly PetPuppet[][] drawn = NewPuppets();

        private static Entry[][] NewEntries()
        {
            Entry[][] e = new Entry[Protocol.MaxPlayers][];
            for (int i = 0; i < e.Length; i++)
            {
                e[i] = new Entry[MaxExtras];
                for (int k = 0; k < MaxExtras; k++)
                    e[i][k] = new Entry();
            }
            return e;
        }

        private static PetPuppet[][] NewPuppets()
        {
            PetPuppet[][] p = new PetPuppet[Protocol.MaxPlayers][];
            for (int i = 0; i < p.Length; i++)
            {
                p[i] = new PetPuppet[MaxExtras];
                for (int k = 0; k < MaxExtras; k++)
                    p[i][k] = new PetPuppet();
            }
            return p;
        }

        public static void Init(NetSession netSession)
        {
            session = netSession;
        }

        // Companions after the first that are out and visible.
        private static void ExtrasOf(PlayerController player, List<AIActor> into)
        {
            into.Clear();
            if (player == null || player.companions == null)
                return;
            bool first = true;
            foreach (AIActor c in player.companions)
            {
                if (c == null || c.sprite == null || !c.gameObject.activeInHierarchy || c.sprite.renderer == null || !c.sprite.renderer.enabled)
                    continue;
                if (first)
                {
                    first = false; // sent with the player's own updates
                    continue;
                }
                into.Add(c);
                if (into.Count >= MaxExtras)
                    break;
            }
        }
        private static readonly List<AIActor> scratch = new List<AIActor>();

        public static void Update()
        {
            bool connected = session != null && session.Status == NetSession.State.Connected;
            if (connected && Time.realtimeSinceStartup >= nextSendAt && GameManager.HasInstance)
            {
                nextSendAt = Time.realtimeSinceStartup + SendInterval;
                Send(GameManager.Instance.PrimaryPlayer, -1);
                if (StandIn.Active && StandIn.Player != null)
                    Send(StandIn.Player, StandIn.Slot);
            }
            Draw(connected);
        }

        private static void Send(PlayerController player, int asSlot)
        {
            ExtrasOf(player, scratch);
            // Nothing to show and nothing shown before: don't send.
            if (scratch.Count == 0 && lastSentCount == 0 && asSlot < 0)
                return;
            if (asSlot < 0)
                lastSentCount = scratch.Count;
            int level = LevelSync.CurrentLevelCode();
            int n = scratch.Count;
            string[] guids = new string[n], clips = new string[n];
            int[] frames = new int[n];
            bool[] flips = new bool[n];
            Vector2[] feet = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                AIActor c = scratch[i];
                guids[i] = c.EnemyGuid ?? "";
                tk2dSpriteAnimator anim = c.spriteAnimator;
                clips[i] = anim != null && anim.CurrentClip != null ? anim.CurrentClip.name : "";
                frames[i] = anim != null && anim.CurrentClip != null ? anim.CurrentFrame : 0;
                flips[i] = c.sprite.FlipX;
                feet[i] = c.sprite.WorldBottomCenter;
            }
            if (asSlot >= 0)
                Protocol.SendAs = asSlot;
            try
            {
                session.SendUnreliable(MsgType.Companions, delegate (BinaryWriter w)
                {
                    w.Write(level);
                    w.Write((byte)n);
                    for (int i = 0; i < n; i++)
                    {
                        w.Write(guids[i]);
                        w.Write(clips[i]);
                        w.Write(frames[i]);
                        w.Write(flips[i]);
                        w.Write(feet[i].x); w.Write(feet[i].y);
                    }
                });
            }
            finally
            {
                Protocol.SendAs = -1;
            }
        }

        public static void OnCompanions(BinaryReader r)
        {
            int slot = session.From;
            int level = r.ReadInt32();
            int n = Mathf.Min(r.ReadByte(), MaxExtras);
            if (slot < 0 || slot >= Protocol.MaxPlayers)
                return;
            for (int i = 0; i < n; i++)
            {
                Entry e = entries[slot][i];
                string guid = r.ReadString();
                if (guid != e.Guid)
                    e.HasShown = false;
                e.Guid = guid;
                e.Clip = r.ReadString();
                e.Frame = r.ReadInt32();
                e.FlipX = r.ReadBoolean();
                e.Target = new Vector2(r.ReadSingle(), r.ReadSingle());
            }
            counts[slot] = n;
            scenes[slot] = level;
            receivedAt[slot] = Time.realtimeSinceStartup;
        }

        private static void Draw(bool connected)
        {
            int level = connected ? LevelSync.CurrentLevelCode() : 0;
            for (int slot = 0; slot < Protocol.MaxPlayers; slot++)
            {
                bool showPlayer = connected && session.LocalSlot != slot && scenes[slot] == level
                    && Time.realtimeSinceStartup - receivedAt[slot] < 1f && !LoopSync.TubePlaying
                    && Plugin.Puppets[slot] != null && Plugin.Puppets[slot].Status.StartsWith("shown");
                for (int k = 0; k < MaxExtras; k++)
                {
                    Entry e = entries[slot][k];
                    bool show = showPlayer && k < counts[slot];
                    if (show)
                    {
                        // Glide to the reported spot (reports come 10x a second).
                        e.Shown = e.HasShown && Vector2.Distance(e.Shown, e.Target) < 4f
                            ? Vector2.Lerp(e.Shown, e.Target, Mathf.Clamp01(Time.deltaTime * 15f)) : e.Target;
                        e.HasShown = true;
                    }
                    try
                    {
                        drawn[slot][k].Show(show, e.Guid, e.Clip, e.Frame, e.FlipX, e.Shown);
                    }
                    catch (System.Exception ex)
                    {
                        Plugin.Log.LogWarning("Couldn't draw a companion: " + ex.Message);
                        counts[slot] = 0;
                    }
                }
            }
        }
    }
}
