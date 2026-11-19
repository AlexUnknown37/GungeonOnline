// Audit test helper (never shipped): keeps a test copy of the game in a small window.
// The game applies its saved fullscreen setting shortly after start, so this re-applies the small window once a second.
using BepInEx;
using UnityEngine;

[BepInPlugin("audit.smallwindow", "Audit Small Window", "1.0.0")]
public class AuditSmallWindow : BaseUnityPlugin
{
    private float next;

    private void Update()
    {
        if (Time.realtimeSinceStartup < next)
            return;
        next = Time.realtimeSinceStartup + 1f;
        if (Screen.fullScreen || Screen.width != 960 || Screen.height != 540)
            Screen.SetResolution(960, 540, false); // an exact 2x of the game's 480x270: other sizes broke the death screen
        // Silent: the sound engine's three volume dials go to zero (the saved options are not touched).
        try
        {
            AkSoundEngine.SetRTPCValue("VOL_MUS", 0f);
            AkSoundEngine.SetRTPCValue("VOL_SFX", 0f);
            AkSoundEngine.SetRTPCValue("VOL_UI", 0f);
        }
        catch (System.Exception) { }
    }
}
