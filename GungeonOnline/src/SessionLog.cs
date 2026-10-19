using System;
using System.IO;
using BepInEx;
using BepInEx.Logging;

namespace GungeonOnline
{
    // A copy of everything in the log (ours, the game's and other mods'), one file per game session, kept in
    // BepInEx/GungeonOnline-logs. The game's own LogOutput.log starts over every time the game starts, so a problem
    // in a session was gone as soon as the game was opened again. The newest 10 sessions are kept.
    public class SessionLog : ILogListener
    {
        private const int Keep = 10;
        private StreamWriter writer;
        private static SessionLog current;

        // Straight into the session log file, from any thread (the freeze watcher runs while the game is stuck).
        public static void Write(string line)
        {
            SessionLog log = current;
            if (log == null || log.writer == null)
                return;
            lock (log)
            {
                try { log.writer.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + line); }
                catch (Exception) { }
            }
        }

        public static string Folder
        {
            get { return Path.Combine(Paths.BepInExRootPath, "GungeonOnline-logs"); }
        }

        public static void Start()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                // Oldest first; leave room for this session.
                string[] old = Directory.GetFiles(Folder, "session-*.log");
                Array.Sort(old, StringComparer.Ordinal);
                for (int i = 0; i < old.Length - (Keep - 1); i++)
                {
                    try { File.Delete(old[i]); }
                    catch (Exception) { } // still open in another copy of the game
                }
                // Copies of the game run at the same time (testing) each get their own file.
                string name = "session-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "-"
                    + System.Diagnostics.Process.GetCurrentProcess().Id + ".log";
                SessionLog log = new SessionLog();
                log.writer = new StreamWriter(new FileStream(Path.Combine(Folder, name), FileMode.Create, FileAccess.Write,
                    FileShare.ReadWrite));
                log.writer.AutoFlush = true; // a crash still leaves everything up to it in the file
                current = log;
                Logger.Listeners.Add(log);
                Plugin.Log.LogInfo("Saving this session's log to " + Path.Combine(Folder, name));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Couldn't start the session log: " + e.Message);
            }
        }

        public void LogEvent(object sender, LogEventArgs eventArgs)
        {
            if (writer == null)
                return;
            lock (this)
            {
                try
                {
                    writer.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + eventArgs.ToString());
                }
                catch (Exception)
                {
                    // Never let the log break the game.
                }
            }
        }

        public void Dispose()
        {
            if (writer != null)
            {
                try { writer.Dispose(); }
                catch (Exception) { }
                writer = null;
            }
        }
    }
}
