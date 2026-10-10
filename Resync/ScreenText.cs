using System;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Vfx;

namespace SlayTheDivergence.Resync;

/// <summary>Non-blocking on-screen notice, using the same fullscreen text VFX the game uses for debug hotkeys.</summary>
public static class ScreenText
{
    public static void Show(string text)
    {
        if (!Settings.ShowOnScreenText)
        {
            return;
        }
        try
        {
            NGame.Instance?.AddChildSafely(NFullscreenTextVfx.Create(text));
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not show on-screen text: {e.Message}");
        }
    }
}
