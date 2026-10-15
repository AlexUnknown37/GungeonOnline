# Shared by the tools below: finds the game window (titled "Enter the Gungeon", not the BepInEx console) of the
# Index-th running copy (0 = the copy started first). Dot-source it, then use $GameWindow.
param([int]$Index = 0)
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class EtgFind {
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    public static IntPtr Find(uint pid) {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr l) {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            StringBuilder s = new StringBuilder(256); GetWindowText(h, s, 256);
            if (s.ToString() == "Enter the Gungeon") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
$procs = @(Get-Process EtG -ErrorAction Stop | Sort-Object StartTime)
if ($Index -ge $procs.Count) { throw "Only $($procs.Count) copies are running." }
$GameWindow = [EtgFind]::Find([uint32]$procs[$Index].Id)
if ($GameWindow -eq [IntPtr]::Zero) { throw "Copy $Index has no game window yet." }
