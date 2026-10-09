using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using HostStateResync.Resync;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Messages.Game.Checksums;
using MegaCrit.Sts2.Core.Runs;

namespace HostStateResync.Patches;

/// <summary>
/// Vanilla flow on a checksum mismatch:
/// <list type="number">
/// <item>host: <c>ChecksumTracker.CompareChecksums</c> sees the mismatch and sends the client a
/// <see cref="StateDivergenceMessage"/> carrying the host's full <see cref="NetFullCombatState"/> at that checksum;</item>
/// <item>client: <c>OnReceivedStateDivergenceMessage</c> logs both states, echoes its own state back to the host and
/// fires <c>StateDiverged</c> (which on the client only writes a replay);</item>
/// <item>host: <c>OnReceivedStateDivergenceMessage</c> logs both states, fires <c>StateDiverged</c> (error popup) and
/// calls <c>DisconnectClient(sender, NetError.StateDivergence)</c>, which is what throws the client back to the menu.</item>
/// </list>
/// This patch replaces step 2 and 3's tail: the client applies the host's snapshot to its own live state
/// (<see cref="HostStateApplier"/>) and the host keeps the client connected unless it keeps diverging.
/// Logging, Sentry reporting and RitsuLib's diagnostics hooks on <c>LogStateDivergence</c> are untouched because the
/// original private method is still invoked.
/// </summary>
[HarmonyPatch(typeof(ChecksumTracker), "OnReceivedStateDivergenceMessage")]
public static class OnReceivedStateDivergenceMessagePatch
{
    private static readonly FieldInfo? NetServiceField = AccessTools.Field(typeof(ChecksumTracker), "_netService");
    private static readonly FieldInfo? ChecksumsField = AccessTools.Field(typeof(ChecksumTracker), "_checksums");
    private static readonly MethodInfo? LogStateDivergenceMethod = AccessTools.Method(typeof(ChecksumTracker), "LogStateDivergence");
    private static readonly Type? TrackedChecksumType = AccessTools.Inner(typeof(ChecksumTracker), "TrackedChecksum");
    private static readonly FieldInfo? TrackedDataField = TrackedChecksumType != null ? AccessTools.Field(TrackedChecksumType, "data") : null;
    private static readonly FieldInfo? TrackedFullStateField = TrackedChecksumType != null ? AccessTools.Field(TrackedChecksumType, "fullState") : null;

    private static bool ReflectionOk =>
        NetServiceField != null && ChecksumsField != null && LogStateDivergenceMethod != null &&
        TrackedDataField != null && TrackedFullStateField != null;

    /// <summary>
    /// Set while the host has decided to fall back to the vanilla kick, so <see cref="RunManagerStateDivergedPatch"/>
    /// lets the vanilla error popup through.
    /// </summary>
    public static bool VanillaHandlingInProgress { get; private set; }

    public static bool Prefix(ChecksumTracker __instance, StateDivergenceMessage message, ulong senderId)
    {
        if (!ReflectionOk)
        {
            MainFile.Logger.Warn("ChecksumTracker internals changed; falling back to vanilla divergence handling");
            return true;
        }

        var netService = NetServiceField!.GetValue(__instance) as INetGameService;
        if (netService == null || netService.Type == NetGameType.Replay || netService.Type == NetGameType.Singleplayer)
        {
            return true;
        }

        // Everything before the first strike or the vanilla logging is read-only. After that, handing over to
        // vanilla would run those side effects a second time (another echo, another strike, a kick on the host).
        bool committed = false;
        try
        {
            uint id = message.senderChecksum.id;
            var checksums = (IList)ChecksumsField!.GetValue(__instance)!;
            int index = -1;
            object? local = null;
            for (int i = 0; i < checksums.Count; i++)
            {
                var data = (NetChecksumData)TrackedDataField!.GetValue(checksums[i])!;
                if (data.id == id)
                {
                    index = i;
                    local = checksums[i];
                    break;
                }
            }

            // A resync already applied makes every checkpoint taken before it stale: their snapshots still carry the
            // drift, so merging them would subtract it a second time, and each echo would cost the client a strike.
            if (netService.Type == NetGameType.Client && id < ResyncFence.Of(__instance))
            {
                MainFile.Logger.Info($"Ignoring divergence #{id}: it predates the resync applied before checksum #{ResyncFence.Of(__instance)}");
                return false;
            }

            bool kick = false;
            committed = true;
            if (netService.Type == NetGameType.Host)
            {
                int strikes = DivergenceStrikes.Record(senderId);
                kick = strikes > Settings.MaxConsecutiveStrikes;
                MainFile.Logger.Warn(kick
                    ? $"Client {senderId} diverged {strikes} times in a row without a matching checksum; giving up and disconnecting it (vanilla behaviour)"
                    : $"Client {senderId} diverged at checksum {id} (strike {strikes}/{Settings.MaxConsecutiveStrikes}); keeping it connected, it will resync from our state");
            }

            VanillaHandlingInProgress = kick;
            try
            {
                if (index < 0)
                {
                    MegaCrit.Sts2.Core.Logging.Log.Error(
                        $"Received state divergence message for checksum ID {id} that has fallen out of our list! (Our next ID: {__instance.NextId})");
                }
                else
                {
                    // Vanilla logging, Sentry report, echo to host, StateDiverged event. RitsuLib hooks here too.
                    LogStateDivergenceMethod!.Invoke(__instance, new[] { local!, message, senderId, index });
                }
            }
            finally
            {
                VanillaHandlingInProgress = false;
            }

            if (netService.Type == NetGameType.Host)
            {
                if (kick)
                {
                    (netService as NetHostGameService)?.DisconnectClient(senderId, NetError.StateDivergence);
                    DivergenceStrikes.Forget(senderId);
                }
                else
                {
                    ScreenText.Show($"Player {senderId} desynced at checksum #{id}, resyncing them from our state");
                }
                return false;
            }

            // Client: heal ourselves from the host's snapshot.
            var baseState = index >= 0 ? TrackedFullStateField!.GetValue(local) as NetFullCombatState : null;
            if (baseState == null)
            {
                // Without our own snapshot of that checkpoint there is nothing to merge against: the host values are
                // 20+ checkpoints old by now. The next divergence still in our history repairs it properly.
                MainFile.Logger.Warn($"No local snapshot for checksum #{id} any more; skipping this resync");
                return false;
            }
            var runState = RunManager.Instance.DebugOnlyGetState();
            if (runState == null)
            {
                MainFile.Logger.Warn("No run state to resync into");
                return false;
            }

            var result = HostStateApplier.Apply(runState, message.senderCombatState, baseState, id,
                CardIdLayouts.TakeFromHost(id), CardIdLayouts.GetLocal(id));
            if (result.FixedCount > 0)
            {
                ResyncFence.Set(__instance, __instance.NextId);
            }
#if DEBUG
            Testing.HsrConsoleCmd.LastResult = result;
#endif
            MainFile.Logger.Info(result.ToLogString());
            ScreenText.Show(result.ToScreenString());
            return false;
        }
        catch (Exception e)
        {
            if (committed)
            {
                MainFile.Logger.Error($"Resync handling failed after the divergence was already handled: {e}");
                return false;
            }
            MainFile.Logger.Warn($"Resync handling failed, falling back to vanilla: {e}");
            return true;
        }
    }
}

/// <summary>
/// Host side: a matching checksum from a client means that client is in sync again, so its strike counter resets.
/// <c>CompareChecksums(TrackedChecksum localChecksum, NetChecksumData remoteChecksum, ulong remoteId)</c>;
/// the first parameter is a private nested struct, hence <c>object</c>.
/// </summary>
[HarmonyPatch(typeof(ChecksumTracker), "CompareChecksums")]
public static class CompareChecksumsPatch
{
    private static readonly Type? TrackedChecksumType = AccessTools.Inner(typeof(ChecksumTracker), "TrackedChecksum");
    private static readonly FieldInfo? TrackedDataField = TrackedChecksumType != null ? AccessTools.Field(TrackedChecksumType, "data") : null;
    private static readonly FieldInfo? NetServiceField = AccessTools.Field(typeof(ChecksumTracker), "_netService");

    /// <summary>
    /// On a mismatch, send the client our card IDs at that checksum before vanilla sends the
    /// <see cref="StateDivergenceMessage"/>; same reliable channel, so they arrive in that order.
    /// </summary>
    public static void Prefix(ChecksumTracker __instance, object localChecksum, NetChecksumData remoteChecksum, ulong remoteId)
    {
        try
        {
            if (TrackedDataField == null || NetServiceField?.GetValue(__instance) is not INetGameService netService ||
                netService.Type != NetGameType.Host)
            {
                return;
            }
            var local = (NetChecksumData)TrackedDataField.GetValue(localChecksum)!;
            if (local.id != remoteChecksum.id || local.checksum == remoteChecksum.checksum)
            {
                return;
            }
            var layout = CardIdLayouts.GetLocal(local.id);
            if (layout == null)
            {
                MainFile.Logger.Warn($"No card ID layout recorded for checksum #{local.id}; client {remoteId} will rebuild piles by value");
                return;
            }
            netService.SendMessage(new HsrCardIdLayoutMessage { checksumId = local.id, layout = layout.Value }, remoteId);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not send card ID layout: {e.Message}");
        }
    }

    public static void Postfix(object localChecksum, NetChecksumData remoteChecksum, ulong remoteId)
    {
        if (TrackedDataField == null)
        {
            return;
        }
        var local = (NetChecksumData)TrackedDataField.GetValue(localChecksum)!;
        if (local.id == remoteChecksum.id && local.checksum == remoteChecksum.checksum)
        {
            DivergenceStrikes.Reset(remoteId);
        }
    }
}

/// <summary>
/// <c>RunManager.StateDiverged</c> is the <c>ChecksumTracker.StateDiverged</c> subscriber. On the host it opens a modal
/// error popup, on the client it logs "abandoning run" and writes a replay. Neither fits a session that continues, so
/// it is skipped unless the host has decided to kick after all. The replay is still written on the client because it
/// is useful for diagnosing what went wrong.
/// </summary>
[HarmonyPatch(typeof(RunManager), "StateDiverged")]
public static class RunManagerStateDivergedPatch
{
    public static bool Prefix(RunManager __instance, ulong divergedFrom, NetFullCombatState state)
    {
        if (OnReceivedStateDivergenceMessagePatch.VanillaHandlingInProgress)
        {
            return true;
        }
        var type = __instance.NetService?.Type;
        if (type == NetGameType.Replay || type == null)
        {
            return true;
        }
        if (type == NetGameType.Client)
        {
            try
            {
                __instance.WriteReplay(stopRecording: false);
            }
            catch (Exception e)
            {
                MainFile.Logger.Warn($"Could not write divergence replay: {e.Message}");
            }
        }
        return false;
    }
}

/// <summary>Per-client counter of divergences without a matching checksum in between. Host only.</summary>
public static class DivergenceStrikes
{
    private static readonly Dictionary<ulong, int> Strikes = new();

    public static int Record(ulong clientId)
    {
        Strikes.TryGetValue(clientId, out int n);
        n++;
        Strikes[clientId] = n;
        return n;
    }

    /// <summary>A new run starts every client from zero; strikes from the last run say nothing about this one.</summary>
    public static void Clear() => Strikes.Clear();

    /// <summary>The client was kicked; if it rejoins, it starts from zero.</summary>
    public static void Forget(ulong clientId) => Strikes.Remove(clientId);

    public static string Describe() => Strikes.Count == 0 ? "none" : string.Join(", ", Strikes.Select(kv => $"{kv.Key}: {kv.Value}"));

    public static void Reset(ulong clientId)
    {
        if (Strikes.Remove(clientId))
        {
            MainFile.Logger.Info($"Client {clientId} produced a matching checksum again; divergence strikes reset");
        }
    }
}

/// <summary>
/// Client only: the first checksum ID generated after the last resync that changed anything. Divergence messages for
/// earlier IDs describe the state before that resync. Kept per <see cref="ChecksumTracker"/>, because every run gets a
/// new tracker whose IDs start at 0 again.
/// </summary>
public static class ResyncFence
{
    private static readonly ConditionalWeakTable<ChecksumTracker, StrongBox<uint>> Fences = new();

    public static uint Of(ChecksumTracker tracker) => Fences.TryGetValue(tracker, out var box) ? box.Value : 0;

    public static void Set(ChecksumTracker tracker, uint nextId) => Fences.AddOrUpdate(tracker, new StrongBox<uint>(nextId));
}
