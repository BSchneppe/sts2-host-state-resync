using System;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace HostStateResync;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "HostStateResync";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.GameSync);

    public static void Initialize()
    {
        try
        {
            new Harmony(ModId).PatchAll(typeof(MainFile).Assembly);
            Logger.Info($"{ModId} initialized: divergent clients are resynced from the host instead of disconnected " +
                        $"(max {Settings.MaxConsecutiveStrikes} consecutive unhealed divergences before falling back to a kick)");
        }
        catch (Exception e)
        {
            Logger.Warn($"{ModId} failed to patch: {e}");
        }
    }
}

public static class Settings
{
    /// <summary>
    /// How many divergence reports the host tolerates from one client without a matching checksum in between.
    /// A resync that works is followed by matching checksums, which resets the counter. A client that keeps
    /// drifting every single checkpoint (something on its side is nondeterministic) is kicked after this many
    /// consecutive divergences, exactly as vanilla would have done on the first one.
    /// </summary>
    public const int MaxConsecutiveStrikes = 6;

    /// <summary>Show a short on-screen text when a resync happens (both on host and client).</summary>
    public static bool ShowOnScreenText = true;
}
