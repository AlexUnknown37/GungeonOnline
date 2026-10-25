using System;
using System.IO;
using UnityEngine;

namespace GungeonOnline
{
    // The first byte of every packet says what kind of message it is; the second which player sent it
    // (their slot: 0 = host, 1-3 = guests). Guests only talk to the host, which passes their messages on.
    public enum MsgType : byte
    {
        Hello = 1,       // "I'm here": protocol version + Steam name. Sent once by each side.
        Ping = 2,        // carries the sender's clock, echoed back as Pong to measure lag
        Pong = 3,
        PlayerState = 4, // position + aim of the sender's player, sent many times a second
        Bye = 5,         // "I'm leaving"
        LevelLoad = 6,   // host -> joiner: load this level with this seed
        ReturnToFoyer = 7, // host -> joiner: go back to the Breach
        Shot = 8,        // a bullet our gun fired: the partner spawns the same one
        EndRun = 9,      // host -> joiner: the run is over (quit or died), unload it and go to the Breach
        PlayerDied = 10, // we died and became a ghost (partner still alive)
        Revive = 11,     // the living player opened a chest or beat the boss: the ghost comes back there
        TeamWiped = 12,  // the living player died too: game over for the ghost as well
        Rules = 13,      // host -> joiner: lobby rules (PvP on/off)
        EnemyDamage = 14, // damage we dealt to an enemy (by ID), applied to the partner's copy
        EnemyDied = 15,  // an enemy died in our game
        EnemyStates = 16, // positions + health of enemies in the room the (living) sender is in
        PullGhost = 17,  // a fight started where we are: our ghost partner warps to us
        Throw = 18,      // we threw/placed an item (Molotov, grenade...): the partner spawns the same one
        PetStart = 19,   // we started petting the partner's dog (their real dog plays its pet animation)
        PetStop = 20,    // we stopped petting it
        Blank = 21,      // we triggered a blank (or ghost pulse): the partner triggers the same one
        RoomCleared = 22, // a room opened because it was cleared: the partner removes what's left there
        Paused = 23,     // our pause menu opened/closed: the partner's game freezes/resumes
        Break = 24,      // a breakable (barrel, crate, ...) or table broke in our game
        TableFlip = 25,  // we flipped a table
        TablePositions = 26, // tables we pushed, by where they started
        Kick = 27,       // we kicked a rolling barrel: the partner replays the kick
        ChestOpened = 29, // we opened a chest: the partner opens their copy (with their own contents)
        HitInRoom = 30,  // we got hit in this room (for the boss's Master Round)
        GunDropped = 31, // we dropped a gun: the partner gets a copy to pick up
        GunTaken = 32,   // we picked up a dropped gun: the partner's copy goes away
        ShopItemGone = 33, // we bought/stole a shop item: sold out for the partner too
        Wallet = 34,     // our money/keys changed by this much: the shared purse changes for the partner too
        LoopState = 35,  // host -> joiner: Loop The Gungeon's loop number and difficulty settings
        Orbitals = 36,   // what floats around our player (Guon Stones...) and where: the partner draws copies
        Inventory = 37,  // joiner -> host: what we carry (the host remembers it for a rejoin)
        InventoryRequest = 38, // joiner -> host: we're back on your run, do you remember our items?
        InventoryRestore = 39, // host -> joiner: your items from before
        RejoinOffer = 40, // host -> joiner: we remember you in this run (your character); rejoin automatically
        FloorPrint = 41, // host -> joiner: fingerprint of our floor (every room's name/position/size)
        Reload = 42,     // we reloaded: the partner plays the reload sound
        Courses = 43,    // our steered/bouncing bullets: where they are and where they head now
        Beams = 44,      // our gun's beams: kind, start point, direction
        AtExit = 45,     // joiner -> host: we took the exit and are waiting for you to take yours
        ModSettings = 46, // host -> joiner: other mods' settings (Gungeon Go Vroom) to match
        Roster = 47,      // host -> guests: who is in the game (slot + name of every player)
        RestartRequest = 48, // guest -> host: we pressed Quick Restart; please restart the run
        WalletState = 49, // host -> guests: the shared purse as it is (money, keys); guests set theirs to it
        KeyItem = 55,     // we got a part of the Bullet That Can Kill The Past (or the Bullet): everyone gets one
        DuelRequest = 56, // guest -> host: we fired the Gun That Can Kill The Past; start the duel
        DuelWon = 57,     // host -> guests: the duel's winner (slot, or -1 for nobody)
        EnemyEffect = 54, // one of our bullets put a status effect (freeze, charm, fire...) on an enemy
        Motion = 53,      // our flying bullets that started circling us or spiralling: the copies do the same
        Companions = 52,  // our extra companions (after the first): kind, animation frame, facing, position
        LoopStart = 51,   // host -> guests: our loop's time tube just started (the loop number it leads to)
        AttackStart = 50, // an enemy in the room we lead started an attack: which one, at whom, from where, dice seed
        Progress = 28,   // host -> joiner: one part of the host's save progress (floors are built from it)
    }

    public static class Protocol
    {
        // Bump this whenever the packet format changes, so old and new versions refuse to talk.
        public const int Version = 48;
        public const int Channel = 0;
        public const int MaxPlayers = 4;
        public const byte NoSlot = 255;   // a guest before the host has given it a slot

        // Our own slot, written into every message we build.
        public static byte LocalSlot = 0;
        // Host: while >= 0, messages are built as coming from this slot (a stand-in playing for someone).
        public static int SendAs = -1;

        // One reusable buffer (messages are built one at a time on the main thread): a new one per message made
        // a lot of garbage.
        private static readonly MemoryStream buildStream = new MemoryStream(1024);
        private static readonly BinaryWriter buildWriter = new BinaryWriter(buildStream);

        // Positions and small offsets in half-pixel steps (1/32 tile) as 16-bit numbers: +-1000 tiles.
        public static short Q(float v)
        {
            return (short)Mathf.Clamp(Mathf.RoundToInt(v * 32f), short.MinValue, short.MaxValue);
        }

        public static float DQ(short v)
        {
            return v / 32f;
        }

        public static void WriteQ(BinaryWriter w, Vector2 v)
        {
            w.Write(Q(v.x));
            w.Write(Q(v.y));
        }

        public static Vector2 ReadQ(BinaryReader r)
        {
            return new Vector2(DQ(r.ReadInt16()), DQ(r.ReadInt16()));
        }

        // A short code for a text that's the same in every game (string.GetHashCode isn't promised to be).
        public static int Hash(string text)
        {
            unchecked
            {
                int h = (int)2166136261;
                if (text != null)
                {
                    foreach (char c in text)
                        h = (h ^ c) * 16777619;
                }
                return h;
            }
        }

        public static byte[] Build(MsgType type, Action<BinaryWriter> writeBody)
        {
            buildStream.SetLength(0);
            buildWriter.Write((byte)type);
            buildWriter.Write(SendAs >= 0 ? (byte)SendAs : LocalSlot);
            if (writeBody != null)
                writeBody(buildWriter);
            buildWriter.Flush();
            return buildStream.ToArray();
        }
    }
}
