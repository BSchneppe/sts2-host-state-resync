using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Platform;
using SlayTheDivergence.Resync;

namespace SlayTheDivergence.Ui;

/// <summary>
/// What the players are told about a resync: one toast per event (repeats of the same event update the visible toast
/// instead of stacking), and an entry in <see cref="ResyncHistory"/>.
/// </summary>
public static class ResyncNotices
{
    private const int MaxDetailLines = 4;

    /// <summary>Strikes from which the host's toast turns into a warning that a kick is coming.</summary>
    private const int WarnFromStrike = 3;

    public static void Resynced(ResyncResult result, INetGameService? net)
    {
        var visible = Details(result.Fixes, net);
        var unfixed = ResyncWording.Unfixed(result.Skips, net?.NetId ?? 0, id => NameOf(id, net));
        if (visible.Count == 0 && unfixed.Count == 0 && result.FixedCount == 0 && result.FailedCount == 0)
        {
            // Nothing written and nothing stuck (say, everything was left for the next checkpoint): the log has it,
            // a toast would only say "nothing happened".
            return;
        }
        var details = visible.Concat(unfixed).ToList();
        if (result.FailedCount > 0) details.Add($"{result.FailedCount} failed, see godot.log");

        string subtitle = visible.Count > 0 ? "Your game drifted; the host's values are back"
            : result.FixedCount > 0 ? "Fixed bookkeeping you can't see (card and choice IDs)"
            : "Some differences can't be repaired";
        Notify(NoticeKind.Resynced, "Resynced with the host", subtitle, details, peer: null, key: "client");
    }

    public static void Repairing(ulong peer, int strikes, INetGameService? net, IReadOnlyList<(string What, string Detail)>? drift = null)
    {
        string name = NameOf(peer, net);
        var details = Details(drift, net);
        if (strikes >= WarnFromStrike)
        {
            Notify(NoticeKind.Warning, $"{name} keeps drifting",
                $"Out of sync {strikes} times in a row. Disconnected after {Settings.MaxConsecutiveStrikes + 1}.",
                details.Append("Another mod is probably making the games drift.").ToList(), peer, $"host:{peer}");
            return;
        }
        Notify(NoticeKind.Repairing, $"{name} drifted out of sync", "Repairing their game from yours. Keep playing.",
            details, peer, $"host:{peer}");
    }

    public static void Kicked(ulong peer, int strikes, INetGameService? net)
    {
        string name = NameOf(peer, net);
        ResyncHistory.Resolve(peer, "disconnected");
        Notify(NoticeKind.Kicked, $"{name} was disconnected", $"Out of sync {strikes} times in a row.",
            new[] { "Another mod is probably making the games drift. Send both players' godot.log." }, peer, $"host:{peer}");
    }

    public static void Recovered(ulong peer, INetGameService? net)
    {
        ResyncHistory.Resolve(peer, "back in sync");
        // Only worth a toast while the drift toast is still up; otherwise the history says it.
        ToastLayer.Morph($"host:{peer}", NoticeKind.Recovered, $"{NameOf(peer, net)} is back in sync", "The repair worked.",
            Array.Empty<string>());
    }

    /// <summary>Debug: the toasts for each event with made-up content, to check the presentation without a desync.</summary>
    public static void Demo(string which)
    {
        const ulong fakePeer = 1001;
        _demo = true;
        try
        {
            DemoNotice(which, fakePeer);
        }
        finally
        {
            _demo = false;
        }
    }

    private static bool _demo;

    private static void DemoNotice(string which, ulong fakePeer)
    {
        switch (which)
        {
            case "client":
                var result = new ResyncResult(42, hadBaseline: true);
                result.Fix("player 0 energy", "2 -> 3");
                result.Fix("player 0 card piles", "rebuilt from host (10 by card ID, 0 by value, 1 cards created, 0 removed)");
                result.Fix("creature PlayerId 0 powers", "POWER.FOCUS_POWER 1 -> 2, +POWER.STRENGTH_POWER 1");
                result.Fix("rng Niche", "counter 3 -> 6");
                result.Skip("player 0 orbs", "the host has 4 orbs, we have 3 slots");
                Resynced(result, null);
                break;
            case "host":
                Repairing(fakePeer, 1, null, FakeDrift);
                break;
            case "warn":
                Repairing(fakePeer, 4, null, FakeDrift);
                break;
            case "kick":
                Kicked(fakePeer, Settings.MaxConsecutiveStrikes + 1, null);
                break;
            case "ok":
                Recovered(fakePeer, null);
                break;
            default:
                throw new ArgumentException($"unknown demo '{which}'");
        }
    }

    private static readonly (string What, string Detail)[] FakeDrift =
    {
        ("player 1001 energy", "5 -> 2"),
        ("player 1001 card piles", "1 cards created, 0 removed"),
        ("creature Nibbit powers", "-POWER.STRENGTH_POWER"),
    };

    public static readonly string[] DemoKinds = { "client", "host", "warn", "kick", "ok" };

    /// <summary>The readable lines for a set of fixes or differences, capped so a toast stays small.</summary>
    private static List<string> Details(IReadOnlyList<(string What, string Detail)>? changes, INetGameService? net)
    {
        if (changes == null) return new List<string>();
        var lines = ResyncWording.Lines(changes, net?.NetId ?? 0, id => NameOf(id, net));
        var details = lines.Take(MaxDetailLines).ToList();
        if (lines.Count > MaxDetailLines) details.Add($"and {lines.Count - MaxDetailLines} more");
        return details;
    }

    private static void Notify(NoticeKind kind, string title, string? subtitle, IReadOnlyList<string> details, ulong? peer, string key)
    {
        if (_demo) title += " (demo)";
        ResyncHistory.Add(new HistoryEntry(kind, title, subtitle, details, peer));
        ToastLayer.Show(key, kind, title, subtitle, details);
    }

    private static string NameOf(ulong id, INetGameService? net)
    {
        try
        {
            if (net != null)
            {
                string name = PlatformUtil.GetPlayerName(net.Platform, id);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch (Exception e)
        {
            MainFile.Logger.Debug($"No name for player {id}: {e.Message}");
        }
        return $"Player {id}";
    }
}
