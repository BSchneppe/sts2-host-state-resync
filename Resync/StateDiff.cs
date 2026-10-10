using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace SlayTheDivergence.Resync;

/// <summary>
/// Host side: what differs between the host's snapshot of a checkpoint and the copy the diverged client sent back, so
/// the host's notice can say what drifted. Read-only; the repair itself happens on the client.
/// </summary>
/// <remarks>
/// Labels and details follow <see cref="HostStateApplier"/>'s fix log ("player 1001 energy" / "5 -> 2", client value
/// first, host value second), so <see cref="Ui.ResyncWording"/> phrases both sides the same way.
/// </remarks>
public static class StateDiff
{
    public static List<(string What, string Detail)> Describe(NetFullCombatState host, NetFullCombatState client)
    {
        var diffs = new List<(string, string)>();

        if (!RngEqual(host.Rng.Rngs, client.Rng.Rngs)) diffs.Add(("rng run", "differs"));

        foreach (var h in host.Players)
        {
            var match = client.Players.Where(p => p.playerId == h.playerId).ToList();
            if (match.Count == 0) continue;
            var c = match[0];
            string who = $"player {h.playerId}";
            Scalar(diffs, $"{who} energy", c.energy, h.energy);
            Scalar(diffs, $"{who} stars", c.stars, h.stars);
            Scalar(diffs, $"{who} gold", c.gold, h.gold);
            Scalar(diffs, $"{who} max potions", c.maxPotionCount, h.maxPotionCount);
            Piles(diffs, who, h, c);
            Ids(diffs, $"{who} potions", c.potions.Select(p => p.id.ToString()), h.potions.Select(p => p.id.ToString()));
            Ids(diffs, $"{who} relics", c.relics.Select(r => r.relic.Id?.ToString() ?? "?"), h.relics.Select(r => r.relic.Id?.ToString() ?? "?"));
            Ids(diffs, $"{who} orbs", c.orbs.Select(o => o.id.ToString()), h.orbs.Select(o => o.id.ToString()));
            if (!RngEqual(h.rngSet.Rngs, c.rngSet.Rngs)) diffs.Add(($"{who} rng streams", "differ"));
        }

        static List<CreatureMatch.Key> Keys(NetFullCombatState s) =>
            s.Creatures.Select(c => new CreatureMatch.Key(c.monsterId, c.playerId, c.currentHp)).ToList();
        var toHost = CreatureMatch.Align(Keys(host), Keys(client));
        if (toHost == null)
        {
            diffs.Add(("creatures", "the host has creatures they don't"));
            return diffs;
        }
        for (int i = 0; i < client.Creatures.Count; i++)
        {
            var c = client.Creatures[i];
            string who = c.playerId != null
                ? $"creature PlayerId {c.playerId}"
                : $"creature {Ui.ResyncWording.Pretty(c.monsterId?.ToString() ?? "monster")}";
            if (toHost[i] < 0)
            {
                diffs.Add(($"{who} removed", "already gone in your game"));
                continue;
            }
            var h = host.Creatures[toHost[i]];
            Scalar(diffs, $"{who} max hp", c.maxHp, h.maxHp);
            Scalar(diffs, $"{who} hp", c.currentHp, h.currentHp);
            Scalar(diffs, $"{who} block", c.block, h.block);
            Powers(diffs, $"{who} powers", c.powers, h.powers);
        }
        if (diffs.Count == 0)
        {
            // What differed is outside what this summary compares (relic counters, card details, turn state).
            diffs.Add(("other", "Other values differ (details in godot.log)"));
        }
        return diffs;
    }

    private static void Scalar(List<(string, string)> diffs, string what, int client, int host)
    {
        if (client != host) diffs.Add((what, $"{client} -> {host}"));
    }

    private static void Piles(List<(string, string)> diffs, string who, NetFullCombatState.PlayerState h, NetFullCombatState.PlayerState c)
    {
        static string Key(NetFullCombatState.CardState card) => $"{card.card.Id}+{card.card.CurrentUpgradeLevel}";
        var hostCards = h.piles.SelectMany(p => p.cards.Select(Key)).ToList();
        var clientCards = c.piles.SelectMany(p => p.cards.Select(Key)).ToList();
        int missing = Missing(clientCards, hostCards);
        int extra = Missing(hostCards, clientCards);
        bool samePlaces = h.piles.Count == c.piles.Count && h.piles.All(hp =>
        {
            var cp = c.piles.Where(p => p.pileType == hp.pileType).ToList();
            return cp.Count == 1 && cp[0].cards.Select(Key).SequenceEqual(hp.cards.Select(Key));
        });
        if (missing > 0 || extra > 0) diffs.Add(($"{who} card piles", $"{missing} cards created, {extra} removed"));
        else if (!samePlaces) diffs.Add(($"{who} card piles", "reordered"));
    }

    /// <summary>How many of <paramref name="want"/> are not in <paramref name="have"/>, counting duplicates.</summary>
    private static int Missing(List<string> have, List<string> want)
    {
        var pool = have.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        int missing = 0;
        foreach (var w in want)
        {
            if (pool.TryGetValue(w, out int n) && n > 0) pool[w] = n - 1;
            else missing++;
        }
        return missing;
    }

    private static void Ids(List<(string, string)> diffs, string what, IEnumerable<string> client, IEnumerable<string> host)
    {
        var c = client.ToList();
        var h = host.ToList();
        if (c.SequenceEqual(h)) return;
        // By count, so a missing second Lightning reads "+Lightning", not "reordered".
        var changes = new List<string>();
        foreach (var id in h.Concat(c).Distinct())
        {
            int more = h.Count(x => x == id) - c.Count(x => x == id);
            for (int k = 0; k < Math.Abs(more); k++) changes.Add((more > 0 ? "+" : "-") + id);
        }
        diffs.Add((what, changes.Count > 0 ? string.Join(", ", changes) : "reordered"));
    }

    private static void Powers(List<(string, string)> diffs, string what, List<NetFullCombatState.PowerState> client, List<NetFullCombatState.PowerState> host)
    {
        var changes = new List<string>();
        foreach (var hp in host)
        {
            var cp = client.Where(p => p.id == hp.id).ToList();
            if (cp.Count == 0) changes.Add($"+{hp.id} {hp.amount}");
            else if (cp[0].amount != hp.amount) changes.Add($"{hp.id} {cp[0].amount} -> {hp.amount}");
        }
        foreach (var cp in client.Where(c => host.All(h => h.id != c.id)))
        {
            changes.Add($"-{cp.id}");
        }
        if (changes.Count == 0 && !client.Select(p => p.id).SequenceEqual(host.Select(p => p.id))) changes.Add("reordered");
        if (changes.Count > 0) diffs.Add((what, string.Join(", ", changes)));
    }

    private static bool RngEqual<TKey>(IReadOnlyDictionary<TKey, SerializableRng> host, IReadOnlyDictionary<TKey, SerializableRng> client)
        where TKey : notnull =>
        host.Count == client.Count &&
        host.All(kv => client.TryGetValue(kv.Key, out var c) && HostStateApplier.BytesEqual(kv.Value, c));
}
