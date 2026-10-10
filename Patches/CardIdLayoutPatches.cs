using System;
using System.Reflection;
using HarmonyLib;
using SlayTheDivergence.Resync;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace SlayTheDivergence.Patches;

/// <summary>
/// Wires <see cref="CardIdLayout"/> into each run's <see cref="ChecksumTracker"/>: every peer records its card IDs at
/// every checkpoint (through the tracker's own <c>ChecksumGenerated</c> event, which fires before any comparison), and
/// listens for the host's <see cref="DivergenceCardIdLayoutMessage"/>. <see cref="CompareChecksumsPatch"/> sends it.
/// </summary>
[HarmonyPatch(typeof(ChecksumTracker), MethodType.Constructor, typeof(INetGameService), typeof(IRunState))]
public static class ChecksumTrackerCtorPatch
{
    private static readonly MessageHandlerDelegate<DivergenceCardIdLayoutMessage> Handler = OnLayoutReceived;

    public static void Postfix(ChecksumTracker __instance, INetGameService netService, IRunState runState)
    {
        try
        {
            CardIdLayouts.Clear();
            DivergenceStrikes.Clear();
            __instance.ChecksumGenerated += (data, _, _) => Record(data, runState);
            netService.RegisterMessageHandler(Handler);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Card ID layouts unavailable, piles will resync by value only: {e}");
        }
    }

    public static void Unregister(INetGameService netService) => netService.UnregisterMessageHandler(Handler);

    private static void Record(NetChecksumData data, IRunState runState)
    {
        try
        {
            var run = runState as RunState ?? RunManager.Instance.DebugOnlyGetState();
            if (run != null) CardIdLayouts.RecordLocal(data.id, run);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not record card IDs for checksum #{data.id}: {e.Message}");
        }
    }

    private static void OnLayoutReceived(DivergenceCardIdLayoutMessage message, ulong senderId)
    {
        CardIdLayouts.ReceiveFromHost(message.checksumId, message.layout);
        MainFile.Logger.Debug($"Host card IDs for checksum #{message.checksumId}: {message.layout}");
    }
}

[HarmonyPatch(typeof(ChecksumTracker), nameof(ChecksumTracker.Dispose))]
public static class ChecksumTrackerDisposePatch
{
    private static readonly FieldInfo? NetServiceField = AccessTools.Field(typeof(ChecksumTracker), "_netService");

    public static void Postfix(ChecksumTracker __instance)
    {
        if (NetServiceField?.GetValue(__instance) is INetGameService netService)
        {
            ChecksumTrackerCtorPatch.Unregister(netService);
        }
    }
}
