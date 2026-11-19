// Audit test helper (never shipped): runs Mod the Gungeon API console commands from a file, so a test script
// can give items, spawn enemies, change floor and so on in a test copy of the game.
// Write one command per line to <this copy>\BepInEx\audit-commands.txt (tools\game-cmd.ps1 does that).
// "wait 2.5" pauses for that many seconds. Each command and any error goes to the BepInEx log.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using UnityEngine;

[BepInPlugin("audit.commands", "Audit Commands", "1.0.0")]
public class AuditCommands : BaseUnityPlugin
{
    private readonly Queue<string> queue = new Queue<string>();
    private string file;
    private float next;

    private void Awake()
    {
        file = Path.Combine(Paths.BepInExRootPath, "audit-commands.txt");
    }

    private void Update()
    {
        if (Time.realtimeSinceStartup < next)
            return;
        next = Time.realtimeSinceStartup + 0.25f;
        try
        {
            if (File.Exists(file))
            {
                string[] lines = File.ReadAllLines(file);
                File.Delete(file);
                foreach (string l in lines)
                    if (l.Trim().Length > 0)
                        queue.Enqueue(l.Trim());
            }
        }
        catch (IOException) { return; } // still being written: try again next tick
        if (queue.Count == 0)
            return;
        string cmd = queue.Dequeue();
        if (cmd.StartsWith("wait "))
        {
            float s;
            if (float.TryParse(cmd.Substring(5), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out s))
                next = Time.realtimeSinceStartup + s;
            return;
        }
        try
        {
            // Found by name so this helper also loads in a copy without the console.
            Type t = Type.GetType("ETGModConsole, ModTheGungeonAPI");
            object console = t == null ? null : t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null, null);
            if (console == null)
            {
                Logger.LogWarning("[cmd] no console yet, dropped: " + cmd);
                return;
            }
            Logger.LogInfo("[cmd] " + cmd);
            t.GetMethod("ParseCommand").Invoke(console, new object[] { cmd });
        }
        catch (Exception e)
        {
            Logger.LogError("[cmd] failed: " + cmd + " : " + (e.InnerException ?? e));
        }
    }
}
