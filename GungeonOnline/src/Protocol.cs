using System;
using System.IO;

namespace GungeonOnline
{
    // The first byte of every packet says what kind of message it is.
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
        Progress = 28,   // host -> joiner: one part of the host's save progress (floors are built from it)
    }

    public static class Protocol
    {
        // Bump this whenever the packet format changes, so old and new versions refuse to talk.
        public const int Version = 24;
        public const int Channel = 0;
        public const int MaxPlayers = 2;

        public static byte[] Build(MsgType type, Action<BinaryWriter> writeBody)
        {
            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((byte)type);
                if (writeBody != null)
                    writeBody(writer);
                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}
