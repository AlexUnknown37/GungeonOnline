// Minimal stand-ins so LanLink.cs / Transport.cs compile and run outside the game.
using System;
namespace UnityEngine
{
    public static class Time { public static float realtimeSinceStartup; }
}
namespace GungeonOnline
{
    public class StubLog
    {
        public void LogInfo(object o) { }
        public void LogWarning(object o) { Console.WriteLine("  [warn] " + o); }
        public void LogError(object o) { Console.WriteLine("  [error] " + o); }
    }
    public static class Plugin
    {
        public static StubLog Log = new StubLog();
        public static string DisplayName() { return "Harness"; }
    }
    public static class Protocol
    {
        public const int Version = 49;
        public const int MaxPlayers = 4;
    }
}
