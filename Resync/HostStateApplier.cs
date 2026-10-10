using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace HostStateResync.Resync;

/// <summary>
/// Writes the host's <see cref="NetFullCombatState"/> snapshot into the client's live run state.
///
/// The snapshot was taken by the host at checksum N. By the time it arrives, the client may already have executed
/// more actions after N (the game is lockstep: both peers run the same action sequence). So the merge is three-way:
/// <c>baseState</c> is the client's own snapshot at N (kept by <c>ChecksumTracker</c> for the last 20 checksums),
/// <c>hostState</c> is the host's snapshot at N, and the live objects are "now".
/// <list type="bullet">
/// <item>Counters (HP, block, energy, gold, power amounts, choice/reward IDs, RNG counters): the local change since N
/// is re-applied on top of the host's value, so <c>target = host + (now - base)</c>.</item>
/// <item>Collections (piles, potions, relics, power set): replaced by the host's version only if they have not changed
/// locally since N. Otherwise the stale snapshot would destroy legitimate local progress; the next checkpoint will
/// deliver a fresher snapshot if they are still different.</item>
/// <item>RNG streams: state is loaded from the host and then advanced by the number of local draws since N.</item>
/// </list>
/// Everything runs through the game's own <c>*Internal</c> mutators so UI events fire; hooks and VFX do not.
/// </summary>
public static class HostStateApplier
{
    private static readonly PacketWriter Writer = new();

    private static readonly Action<Creature, int>? SetCurrentHp = MakeSetter<Creature, int>("CurrentHp");
    private static readonly Action<Creature, int>? SetMaxHp = MakeSetter<Creature, int>("MaxHp");
    private static readonly Action<Creature, int>? SetBlock = MakeSetter<Creature, int>("Block");

    /// <param name="hostIds">The host's combat card IDs at the checksum, when its build sent them. Without them piles
    /// are rebuilt by value only, which can leave identical cards holding each other's IDs.</param>
    /// <param name="baseIds">Our own card IDs at the checksum, the base of the three-way merge for the ID counter.</param>
    public static ResyncResult Apply(RunState run, NetFullCombatState host, NetFullCombatState? baseState, uint checksumId,
        CardIdLayout? hostIds = null, CardIdLayout? baseIds = null)
    {
        var result = new ResyncResult(checksumId, baseState != null);
        var ids = new IdPlan(hostIds, baseIds, CardIdLayout.Capture(run), CardIdDb.NextId);
        if (hostIds == null)
        {
            result.Skip("card ids", "host sent no card ID layout; piles are matched by value only");
        }

        // Turn setup assigns some values outright (ResetEnergy, block clearing) instead of changing them by an amount.
        // Once a player's turn or phase moved on since the checkpoint, a local change to a counter may be one of
        // those resets, and re-applying it as a delta on top of the host's value would be wrong.
        bool turnMoved = baseState != null && baseState.Players.Any(bp =>
            run.GetPlayer(bp.playerId)?.PlayerCombatState is { } c && (c.TurnNumber != bp.turnNumber || c.Phase != bp.phase));

        Try(result, "run RNG", () => ApplyRunRng(run.Rng, host.Rng, baseState?.Rng, result));
        Try(result, "sync markers", () => ApplySyncMarkers(host, baseState, result));

        foreach (var hostPlayer in host.Players)
        {
            var player = run.GetPlayer(hostPlayer.playerId);
            if (player == null)
            {
                result.Skip($"player {hostPlayer.playerId}", "not in local run");
                continue;
            }
            var basePlayer = FindPlayer(baseState, hostPlayer.playerId);
            var hp = hostPlayer;
            Try(result, $"player {player.NetId}", () => ApplyPlayer(run, player, hp, basePlayer, ids, turnMoved, result));
        }

        Try(result, "creatures", () => ApplyCreatures(run, host, baseState, turnMoved, result));
        foreach (var hostPlayer in host.Players)
        {
            if (run.GetPlayer(hostPlayer.playerId)?.PlayerCombatState is not { } combat) continue;
            var hp = hostPlayer;
            var basePlayer = FindPlayer(baseState, hostPlayer.playerId);
            Try(result, $"player {hp.playerId} orbs", () => ApplyOrbs(combat, hp, basePlayer, $"player {hp.playerId}", result));
        }
        // After the creatures: card values (RecalculateCardValues) follow the powers just written.
        Try(result, "card piles", () => CommitPiles(ids, run, result));
        return result;
    }

    // ------------------------------------------------------------------ RNG

    private static void ApplyRunRng(RunRngSet local, SerializableRunRngSet host, SerializableRunRngSet? baseSet, ResyncResult result)
    {
        foreach (var (type, hostRng) in host.Rngs)
        {
            Rng rng = local.GetRng(type);
            var localRng = rng.ToSerializable();
            if (BytesEqual(localRng, hostRng))
            {
                continue;
            }
            SerializableRng? baseRng = baseSet != null && baseSet.Rngs.TryGetValue(type, out var b) ? b : null;
            ApplyRng(rng, hostRng, baseRng, $"rng {type}", result);
        }
    }

    private static void ApplyPlayerRng(PlayerRngSet local, SerializablePlayerRngSet host, SerializablePlayerRngSet? baseSet, string who, ResyncResult result)
    {
        foreach (var (type, hostRng) in host.Rngs)
        {
            Rng rng = local.GetRng(type);
            if (BytesEqual(rng.ToSerializable(), hostRng))
            {
                continue;
            }
            SerializableRng? baseRng = baseSet != null && baseSet.Rngs.TryGetValue(type, out var b) ? b : null;
            ApplyRng(rng, hostRng, baseRng, $"{who} rng {type}", result);
        }
    }

    private const long MaxReplayedDraws = 10_000;

    private static void ApplyRng(Rng rng, SerializableRng host, SerializableRng? baseRng, string what, ResyncResult result)
    {
        if (baseRng != null && BytesEqual(baseRng, host))
        {
            // In step with the host at the checkpoint; every draw since then is our own, legitimately.
            return;
        }
        long nowCounter = Counter(rng.ToSerializable());
        long draws = 0;
        if (baseRng != null)
        {
            draws = nowCounter - Counter(baseRng);
            if (draws < 0)
            {
                result.Skip(what, $"stream was reset since the checkpoint (counter {Counter(baseRng)} -> {nowCounter})");
                return;
            }
            if (draws > MaxReplayedDraws)
            {
                result.Skip(what, $"{draws} local draws since the checkpoint, more than the {MaxReplayedDraws} replay limit");
                return;
            }
        }

        rng.LoadFromSerializable(host);
        // Every public Next* call steps the generator once and bumps the counter once, so replaying the local draws
        // since the checkpoint lands exactly where the host will be after the same actions.
        for (long i = 0; i < draws; i++)
        {
            rng.NextUnsignedLong();
        }
        result.Fix(what, $"counter {nowCounter} -> {Counter(rng.ToSerializable())}" +
                         (draws > 0 ? $" (host {Counter(host)} + {draws} local draws)" : ""));
    }

    private static long Counter(SerializableRng rng) => rng.counter;

    // ---------------------------------------------------------- sync markers

    private static void ApplySyncMarkers(NetFullCombatState host, NetFullCombatState? baseState, ResyncResult result)
    {
        // A list that matched the host at the checkpoint has only moved on through our own actions since: leave it.
        var choiceSync = RunManager.Instance.PlayerChoiceSynchronizer;
        var now = choiceSync.ChoiceIds.ToList();
        if (!now.SequenceEqual(host.nextChoiceIds) && baseState?.nextChoiceIds.SequenceEqual(host.nextChoiceIds) != true)
        {
            var target = MergeCounters<uint>(host.nextChoiceIds, baseState?.nextChoiceIds, now, (h, b, n) => (uint)(h + (n - b)));
            if (target != null)
            {
                choiceSync.FastForwardChoiceIds(target);
                result.Fix("choice ids", $"{Join(now)} -> {Join(target)}");
            }
            else
            {
                result.Skip("choice ids", $"player count differs ({now.Count} vs {host.nextChoiceIds.Count})");
            }
        }

        var rewardSync = RunManager.Instance.RewardsSetSynchronizer;
        var nowRewards = rewardSync.GetNextRewardIds().ToList();
        if (!nowRewards.SequenceEqual(host.nextRewardIds) && baseState?.nextRewardIds.SequenceEqual(host.nextRewardIds) != true)
        {
            var target = MergeCounters<int>(host.nextRewardIds, baseState?.nextRewardIds, nowRewards, (h, b, n) => (int)(h + (n - b)));
            if (target != null)
            {
                rewardSync.FastForwardRewardIds(target);
                result.Fix("reward ids", $"{Join(nowRewards)} -> {Join(target)}");
            }
            else
            {
                result.Skip("reward ids", $"player count differs ({nowRewards.Count} vs {host.nextRewardIds.Count})");
            }
        }
    }

    private static List<T>? MergeCounters<T>(List<T> host, List<T>? baseList, List<T> now, Func<long, long, long, T> merge) where T : struct
    {
        if (host.Count != now.Count)
        {
            return null;
        }
        var target = new List<T>(host.Count);
        for (int i = 0; i < host.Count; i++)
        {
            long h = Convert.ToInt64(host[i]);
            if (baseList != null && baseList.Count == host.Count)
            {
                target.Add(merge(h, Convert.ToInt64(baseList[i]), Convert.ToInt64(now[i])));
            }
            else
            {
                target.Add(host[i]);
            }
        }
        return target;
    }

    // ---------------------------------------------------------------- player

    private static void ApplyPlayer(RunState run, Player player, NetFullCombatState.PlayerState host, NetFullCombatState.PlayerState? baseP, IdPlan ids, bool turnMoved, ResyncResult result)
    {
        string who = $"player {player.NetId}";

        ApplyScalar($"{who} gold", host.gold, baseP?.gold, player.Gold, 0, turnMoved, v => player.Gold = v, result);
        ApplyScalar($"{who} max potions", host.maxPotionCount, baseP?.maxPotionCount, player.MaxPotionCount, 0, turnMoved, v =>
        {
            int diff = v - player.MaxPotionCount;
            if (diff > 0)
            {
                player.AddToMaxPotionCount(diff);
            }
            else if (diff < 0)
            {
                player.SubtractFromMaxPotionCount(-diff);
            }
        }, result);

        ApplyPlayerRng(player.PlayerRng, host.rngSet, baseP?.rngSet, who, result);

        if (!BytesEqual(player.RelicGrabBag.ToSerializable(), host.relicGrabBag))
        {
            if (baseP == null || BytesEqual(player.RelicGrabBag.ToSerializable(), baseP.Value.relicGrabBag))
            {
                player.RelicGrabBag.LoadFromSerializable(host.relicGrabBag);
                result.Fix($"{who} relic grab bag", "loaded host's");
            }
            else
            {
                result.Skip($"{who} relic grab bag", "changed locally since checkpoint");
            }
        }

        ApplyPotions(player, host, baseP, who, result);
        ApplyRelics(player, host, baseP, who, result);

        var combat = player.PlayerCombatState;
        bool hostInCombat = host.piles.Count > 0;
        if (combat == null)
        {
            if (hostInCombat)
            {
                result.Skip($"{who} combat", "host is in combat, we are not");
            }
            return;
        }

        if (combat.TurnNumber != host.turnNumber || combat.Phase != host.phase)
        {
            result.Skip($"{who} turn/phase", $"local turn {combat.TurnNumber} {combat.Phase} vs host turn {host.turnNumber} {host.phase} (flow state is not overridable)");
        }

        ApplyScalar($"{who} energy", host.energy, baseP?.energy, combat.Energy, 0, turnMoved, v => combat.Energy = v, result);
        ApplyScalar($"{who} stars", host.stars, baseP?.stars, combat.Stars, 0, turnMoved, v => combat.Stars = v, result);

        if (hostInCombat)
        {
            PlanPiles(player, combat, host, baseP, ids, who, result);
        }
    }

    private static void ApplyPotions(Player player, NetFullCombatState.PlayerState host, NetFullCombatState.PlayerState? baseP, string who, ResyncResult result)
    {
        // In order: the checksum hashes the potions in slot order.
        var localIds = player.Potions.Select(p => p.Id).ToList();
        var hostIds = host.potions.Select(p => p.id).ToList();
        if (localIds.SequenceEqual(hostIds))
        {
            return;
        }
        if (baseP != null && !localIds.SequenceEqual(baseP.Value.potions.Select(p => p.id)))
        {
            result.Skip($"{who} potions", "changed locally since checkpoint");
            return;
        }

        var changes = new List<string>();
        if (!Same(localIds, hostIds))
        {
            var extra = new List<ModelId>(localIds);
            foreach (var id in hostIds)
            {
                extra.Remove(id);
            }
            var missing = new List<ModelId>(hostIds);
            foreach (var id in localIds)
            {
                missing.Remove(id);
            }

            foreach (var id in extra)
            {
                var potion = player.Potions.First(p => p.Id == id);
                player.DiscardPotionInternal(potion, silent: true);
            }
            foreach (var id in missing)
            {
                var potion = PotionModel.FromSerializable(new SerializablePotion { Id = id });
                player.AddPotionInternal(potion, -1, silent: true);
            }
            changes.Add($"removed [{Join(extra)}], added [{Join(missing)}]");
        }
        if (ReorderPotions(player, hostIds))
        {
            changes.Add("reordered");
        }
        if (changes.Count > 0)
        {
            result.Fix($"{who} potions", string.Join(", ", changes));
        }
    }

    private static readonly FieldInfo? PotionSlotsField = AccessTools.Field(typeof(Player), "_potionSlots");
    private static readonly FieldInfo? RelicsField = AccessTools.Field(typeof(Player), "_relics");
    private static readonly FieldInfo? PowersField = AccessTools.Field(typeof(Creature), "_powers");

    /// <summary>
    /// Puts the potions in the host's order within the slots already occupied. The snapshot does not say which slots
    /// the host's potions sit in, and the network names a potion by its slot index, so moving potions into other slots
    /// could break the next potion use instead.
    /// </summary>
    private static bool ReorderPotions(Player player, List<ModelId> hostOrder)
    {
        if (PotionSlotsField?.GetValue(player) is not List<PotionModel> slots)
        {
            return false;
        }
        var occupied = Enumerable.Range(0, slots.Count).Where(i => slots[i] != null).ToList();
        var ordered = InOrder(occupied.Select(i => slots[i]).ToList(), hostOrder, p => p.Id);
        if (ordered == null)
        {
            return false;
        }
        bool changed = false;
        for (int k = 0; k < occupied.Count; k++)
        {
            if (slots[occupied[k]] != ordered[k])
            {
                slots[occupied[k]] = ordered[k];
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Reorders a private model list in place to the host's order; false if it already was or cannot be.</summary>
    private static bool Reorder<T>(FieldInfo? field, object owner, List<ModelId> hostOrder, Func<T, ModelId> id) where T : class
    {
        if (field?.GetValue(owner) is not List<T> list)
        {
            return false;
        }
        var ordered = InOrder(list, hostOrder, id);
        if (ordered == null || ordered.SequenceEqual(list))
        {
            return false;
        }
        list.Clear();
        list.AddRange(ordered);
        return true;
    }

    /// <summary>The same instances in the host's order (duplicates matched one by one), or null if the sets differ.</summary>
    private static List<T>? InOrder<T>(List<T> items, List<ModelId> hostOrder, Func<T, ModelId> id) where T : class
    {
        if (items.Count != hostOrder.Count)
        {
            return null;
        }
        var pool = items.ToList();
        var ordered = new List<T>();
        foreach (var wanted in hostOrder)
        {
            var match = pool.FirstOrDefault(x => id(x) == wanted);
            if (match == null)
            {
                return null;
            }
            ordered.Add(match);
            pool.Remove(match);
        }
        return ordered;
    }

    private static void ApplyRelics(Player player, NetFullCombatState.PlayerState host, NetFullCombatState.PlayerState? baseP, string who, ResyncResult result)
    {
        var local = player.Relics.Select(r => r.ToSerializable()).ToList();
        var hostRelics = host.relics.Select(r => r.relic).ToList();
        if (ListBytesEqual(local, hostRelics))
        {
            return;
        }
        if (baseP != null && !ListBytesEqual(local, baseP.Value.relics.Select(r => r.relic).ToList()))
        {
            result.Skip($"{who} relics", "changed locally since checkpoint");
            return;
        }

        var changes = new List<string>();
        // Same relic set: only saved properties (counters etc.) differ -> fill them in place.
        var localIds = player.Relics.Select(r => r.Id).ToList();
        var hostIds = hostRelics.Select(r => r.Id!).ToList();
        if (Same(localIds, hostIds))
        {
            var pool = player.Relics.ToList();
            foreach (var hostRelic in hostRelics)
            {
                var relic = pool.First(r => r.Id == hostRelic.Id);
                pool.Remove(relic);
                if (!BytesEqual(relic.ToSerializable(), hostRelic))
                {
                    hostRelic.Props?.Fill(relic);
                    changes.Add($"{relic.Id} props");
                }
            }
        }
        else
        {
            foreach (var relic in player.Relics.Where(r => !hostIds.Contains(r.Id)).ToList())
            {
                player.RemoveRelicInternal(relic, silent: true);
                changes.Add($"-{relic.Id}");
            }
            foreach (var hostRelic in hostRelics.Where(r => !localIds.Contains(r.Id!)))
            {
                player.AddRelicInternal(RelicModel.FromSerializable(hostRelic), -1, silent: true);
                changes.Add($"+{hostRelic.Id}");
            }
        }
        // The checksum hashes relics in order, and AddRelicInternal appends.
        if (Reorder<RelicModel>(RelicsField, player, hostIds, r => r.Id))
        {
            changes.Add("reordered");
        }
        if (changes.Count > 0)
        {
            result.Fix($"{who} relics", string.Join(", ", changes));
        }
    }

    // ------------------------------------------------------------------ orbs

    /// <summary>
    /// Orbs are only put back in the host's order, never created or destroyed. A created orb has no node in
    /// NOrbManager, so evoking it throws (EvokeOrbAnim looks its node up by model) before the evoke takes effect, and it
    /// loses whatever other mods keep per orb instance. Passive and evoke values are computed by hooks (Focus, relics,
    /// other mods' per-orb bonuses), so they are not written either. The exception is state an orb accumulates in its
    /// own private field (Dark's evoke value, Glass's passive value), which is moved by its difference to the host's.
    /// Runs after the creatures, so a Focus difference is already repaired when the values are compared.
    /// </summary>
    private static void ApplyOrbs(PlayerCombatState combat, NetFullCombatState.PlayerState host, NetFullCombatState.PlayerState? baseP, string who, ResyncResult result)
    {
        var queue = combat.OrbQueue;
        var localOrbs = queue.Orbs.Select(NetFullCombatState.OrbState.From).ToList();
        if (ListBytesEqual(localOrbs, host.orbs))
        {
            return;
        }
        if (baseP != null && !ListBytesEqual(localOrbs, baseP.Value.orbs))
        {
            result.Skip($"{who} orbs", "changed locally since checkpoint");
            return;
        }

        // Our orbs in the host's order: an orb with the host's values first, then any orb of that type.
        var pool = queue.Orbs.ToList();
        var desired = new OrbModel?[host.orbs.Count];
        foreach (var exact in new[] { true, false })
        {
            for (int i = 0; i < host.orbs.Count; i++)
            {
                if (desired[i] != null) continue;
                var h = host.orbs[i];
                var match = pool.FirstOrDefault(o => o.Id == h.id && (!exact || BytesEqual(NetFullCombatState.OrbState.From(o), h)));
                if (match != null)
                {
                    desired[i] = match;
                    pool.Remove(match);
                }
            }
        }
        if (desired.Any(o => o == null) || pool.Count > 0)
        {
            result.Skip($"{who} orbs", "the orb set differs; creating or destroying orbs is not repaired " +
                                        "(a created orb has no UI node and loses other mods' per-orb state)");
            return;
        }

        var changes = new List<string>();
        var order = desired.Select(o => o!).ToList();
        if (!order.SequenceEqual(queue.Orbs))
        {
            foreach (var orb in queue.Orbs.ToList())
            {
                queue.Remove(orb);
            }
            for (int i = 0; i < order.Count; i++)
            {
                queue.Insert(i, order[i]);
            }
            changes.Add("reordered");
        }
        for (int i = 0; i < order.Count; i++)
        {
            var orb = order[i];
            if (Nudge(orb, "_evokeVal", host.orbs[i].evoke - (int)orb.EvokeVal)) changes.Add($"{orb.Id} evoke");
            if (Nudge(orb, "_passiveVal", host.orbs[i].passive - (int)orb.PassiveVal)) changes.Add($"{orb.Id} passive");
        }
        if (changes.Count > 0)
        {
            result.Fix($"{who} orbs", string.Join(", ", changes));
        }
        if (!ListBytesEqual(queue.Orbs.Select(NetFullCombatState.OrbState.From).ToList(), host.orbs))
        {
            result.Skip($"{who} orbs", "passive/evoke values still differ; they come from Focus, relics and other mods' " +
                                       "per-orb state, which a resync cannot write");
        }
    }

    /// <summary>
    /// Moves a decimal field the orb type itself declares by <paramref name="delta"/>. Hook modifiers on top of it are
    /// additive, so shifting the field shifts the computed value by the same amount.
    /// </summary>
    private static bool Nudge(OrbModel orb, string field, int delta)
    {
        if (delta == 0) return false;
        var info = orb.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (info == null || info.FieldType != typeof(decimal)) return false;
        info.SetValue(orb, (decimal)info.GetValue(orb)! + delta);
        return true;
    }

    // ----------------------------------------------------------------- piles

    /// <summary>
    /// Plans one player's pile rebuild without touching anything. <see cref="CommitPiles"/> applies every player's plan
    /// together with the card-ID rewrite, or none of it: rebuilt piles with misplaced IDs are worse than no rebuild,
    /// because they hash clean while the next play of a misplaced card is cancelled on the host only.
    /// </summary>
    private static void PlanPiles(Player player, PlayerCombatState combat, NetFullCombatState.PlayerState host, NetFullCombatState.PlayerState? baseP, IdPlan ids, string who, ResyncResult result)
    {
        var localPiles = combat.AllPiles.Where(p => p.IsCombatPile).ToList();
        var combatScope = player.Creature.CombatState;
        if (combatScope == null)
        {
            result.Skip($"{who} card piles", "no combat state to own created cards");
            return;
        }
        var localNow = localPiles.Select(NetFullCombatState.CombatPileState.From).ToList();
        // Equal values are not enough: identical cards can still hold each other's IDs (see CardIdLayout).
        bool idsMatch = ids.Host == null || ids.Now.SamePlacement(ids.Host.Value, player.NetId);
        if (ListBytesEqual(localNow, host.piles) && idsMatch)
        {
            return;
        }
        if (baseP != null && !ListBytesEqual(localNow, baseP.Value.piles))
        {
            result.Skip($"{who} card piles", "changed locally since checkpoint");
            return;
        }
        if (ids.Base != null && !ids.Now.SamePlacement(ids.Base.Value, player.NetId))
        {
            result.Skip($"{who} card piles", "card IDs moved locally since checkpoint");
            return;
        }

        var plan = new PilePlan(player, combat, combatScope, host, who, localPiles);

        // Every slot of the host's piles, in the host's order, with the host's card ID where we have one.
        var slots = new List<(CardPile pile, NetFullCombatState.CardState card, uint id)>();
        foreach (var hostPile in host.piles)
        {
            var pile = localPiles.FirstOrDefault(p => p.Type == hostPile.pileType);
            if (pile == null)
            {
                result.Skip($"{who} pile {hostPile.pileType}", "no such local pile");
                continue;
            }
            var hostIds = ids.Host?.IdsOf(player.NetId, hostPile.pileType);
            if (hostIds != null && hostIds.Count != hostPile.cards.Count)
            {
                result.Skip($"{who} pile {hostPile.pileType} ids", $"{hostIds.Count} IDs for {hostPile.cards.Count} cards");
                hostIds = null;
            }
            for (int i = 0; i < hostPile.cards.Count; i++)
            {
                slots.Add((pile, hostPile.cards[i], hostIds?[i] ?? CardIdLayout.NoId));
            }
        }

        // Hand our cards out to those slots in passes, strictest first and across all slots, so that a loose match for
        // one slot never takes the card a stricter pass would have given another: the same card state (card, affliction,
        // cost, keywords) held under the host's ID, the same state under any ID, the same card and upgrade under the
        // host's ID, the same card and upgrade. Whatever takes a slot under another ID is re-keyed in CommitPiles.
        var db = NetCombatCardDb.Instance;
        var pool = localPiles.SelectMany(p => p.Cards).ToList();
        var states = pool.ToDictionary(c => c, NetFullCombatState.CardState.From);
        var picks = new CardModel?[slots.Count];
        bool Holds(CardModel c, uint id) => id != CardIdLayout.NoId && db.TryGetCard(id, out var holder) && holder == c;
        bool Exact(CardModel c, NetFullCombatState.CardState s) => BytesEqual(states[c], s);
        foreach (var pass in new Func<CardModel, int, bool>[]
                 {
                     (c, i) => Exact(c, slots[i].card) && Holds(c, slots[i].id),
                     (c, i) => Exact(c, slots[i].card),
                     (c, i) => SameCard(c, slots[i].card.card) && Holds(c, slots[i].id),
                     (c, i) => SameCard(c, slots[i].card.card),
                 })
        {
            for (int i = 0; i < slots.Count; i++)
            {
                if (picks[i] != null)
                {
                    continue;
                }
                var match = pool.FirstOrDefault(c => pass(c, i));
                if (match != null)
                {
                    picks[i] = match;
                    pool.Remove(match);
                }
            }
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var (pile, card, id) = slots[i];
            var pick = picks[i];
            if (pick != null && Holds(pick, id))
            {
                plan.ById++;
            }
            else if (pick != null && id != CardIdLayout.NoId)
            {
                plan.ByValue++;
            }
            plan.Slots.Add((pile, pick, card, id));
        }
        plan.Leftovers.AddRange(pool);
        ids.Piles.Add(plan);
    }

    private static bool SameCard(CardModel card, SerializableCard wanted) =>
        card.Id == wanted.Id && card.CurrentUpgradeLevel == wanted.CurrentUpgradeLevel;

    /// <summary>
    /// Applies the planned pile rebuilds and puts every card under the ID the host has for its slot, as one step.
    /// </summary>
    /// <remarks>
    /// The ID rewrite is planned first, against the database as it is: cards the rebuild drops lose their IDs, IDs handed
    /// out locally since the checkpoint move with the counter (<c>host + (now - base)</c>), and a host ID still held by a
    /// card that has left every pile (a played Power, say; the database never forgets a card) is taken from it. If an ID
    /// is held by a card that is still in play and not being moved, nothing is touched at all, piles included.
    /// </remarks>
    private static void CommitPiles(IdPlan ids, RunState run, ResyncResult result)
    {
        uint nowNext = ids.NowNext ?? 0;
        CardIdRewrite? rewrite = null;
        if (ids.Host != null && ids.NowNext != null)
        {
            uint hostNext = ids.Host.Value.nextId;
            if (ids.Base != null && nowNext < ids.Base.Value.nextId)
            {
                // StartCombat resets the counter to 0; this checkpoint belongs to a combat that is over.
                foreach (var plan in ids.Piles)
                {
                    result.Skip($"{plan.Who} card piles", "a new combat started since the checkpoint");
                }
                result.Skip("card ids", "a new combat started since the checkpoint");
                return;
            }
            uint target = ids.Base != null ? (uint)Math.Max(0, (long)hostNext + nowNext - ids.Base.Value.nextId) : Math.Max(hostNext, nowNext);
            long shift = ids.Base != null ? (long)hostNext - ids.Base.Value.nextId : 0;
            uint shiftFrom = ids.Base?.nextId ?? uint.MaxValue;
            if (ids.Piles.Count == 0 && shift == 0 && target == nowNext)
            {
                return;
            }

            var assign = ids.Piles.SelectMany(p => p.Slots).Where(s => s.card != null && s.id != CardIdLayout.NoId)
                .Select(s => (s.card!, s.id)).ToList();
            var reserved = ids.Piles.SelectMany(p => p.Slots).Where(s => s.card == null && s.id != CardIdLayout.NoId)
                .Select(s => s.id).ToList();
            var forget = ids.Piles.SelectMany(p => p.Leftovers).ToHashSet();
            var moving = assign.Select(a => a.Item1).ToHashSet();
            var live = run.Players.Where(p => p.PlayerCombatState != null)
                .SelectMany(p => p.PlayerCombatState!.AllPiles.Where(pile => pile.IsCombatPile).SelectMany(pile => pile.Cards))
                .Where(c => !forget.Contains(c) && !moving.Contains(c)).ToHashSet();
            string? clash = CardIdDb.Plan(assign, reserved, forget, live, shiftFrom, shift, target, out rewrite);
            if (clash != null)
            {
                foreach (var plan in ids.Piles)
                {
                    result.Skip($"{plan.Who} card piles", $"not rebuilt: {clash}");
                }
                result.Skip("card ids", clash);
                return;
            }
        }

        var created = new List<(CardModel card, uint id)>();
        foreach (var plan in ids.Piles)
        {
            var placed = new List<(CardPile pile, CardModel card)>();
            int createdHere = 0;
            foreach (var (pile, card, state, id) in plan.Slots)
            {
                var placedCard = card;
                if (placedCard == null)
                {
                    placedCard = CardModel.FromSerializable(state.card);
                    // The combat scope, as CombatState.CreateCard does: CardPileCmd.Add and manual plays throw for an
                    // in-combat card the CombatState does not contain, so a run-scoped card would break on its next draw.
                    plan.Scope.AddCard(placedCard, plan.Player);
                    createdHere++;
                    if (id != CardIdLayout.NoId)
                    {
                        created.Add((placedCard, id));
                    }
                }
                placed.Add((pile, placedCard));
            }

            foreach (var pile in plan.Piles)
            {
                foreach (var card in pile.Cards.ToList())
                {
                    pile.RemoveInternal(card, silent: true);
                }
            }
            foreach (var (pile, card) in placed)
            {
                pile.AddInternal(card, -1, silent: true);
            }
            foreach (var pile in plan.Piles)
            {
                pile.InvokeContentsChanged();
            }
            plan.Combat.RecalculateCardValues();
            if (LocalContext.IsMe(plan.Player))
            {
                SyncHandView(plan.Combat.Hand, plan.Who, result);
            }
            string matched = ids.Host != null ? $"{plan.ById} by card ID, {plan.ByValue} by value, " : "";
            result.Fix($"{plan.Who} card piles",
                $"rebuilt from host ({matched}{createdHere} cards created, {plan.Leftovers.Count} removed)");

            // Nothing here writes a card's own values, so a card whose cost, keywords, affliction or enchantment differ
            // from every candidate stays different. Say so instead of letting the rebuild read as a full repair.
            var after = plan.Piles.Select(NetFullCombatState.CombatPileState.From).ToList();
            int stillDifferent = after.Zip(plan.Host.piles).Sum(p => p.First.cards.Zip(p.Second.cards).Count(c => !BytesEqual(c.First, c.Second)));
            if (stillDifferent > 0)
            {
                result.Skip($"{plan.Who} card piles", $"{stillDifferent} card(s) still differ in cost, keywords, affliction or enchantment, which a resync cannot write");
            }
        }

        if (rewrite != null)
        {
            int changed = CardIdDb.Commit(rewrite, created);
            uint after = CardIdDb.NextId ?? nowNext;
            if (changed > 0 || rewrite.Evicted > 0 || after != nowNext)
            {
                string evicted = rewrite.Evicted > 0 ? $", {rewrite.Evicted} taken from cards no longer in play" : "";
                result.Fix("card ids", $"{changed} card(s) re-keyed to the host's IDs{evicted}, next ID {nowNext} -> {after}");
            }
        }
    }

    /// <summary>
    /// NPlayerHand builds its card nodes from the pile commands, not from pile events, so a silent rebuild leaves it
    /// showing the old hand: cards now in hand are invisible and unplayable, cards that left stay clickable (a play of
    /// one is cancelled on both peers). When the shown cards differ from the Hand pile, rebuild the holders from it.
    /// </summary>
    private static void SyncHandView(CardPile hand, string who, ResyncResult result)
    {
        var view = NPlayerHand.Instance;
        if (view == null)
        {
            return;
        }
        var shown = view.ActiveHolders.Select(h => h.CardNode?.Model).ToList();
        if (shown.SequenceEqual(hand.Cards))
        {
            return;
        }
        if (view.InCardPlay || view.IsInCardSelection)
        {
            result.Skip($"{who} hand view", "a card play or selection is in progress; the view catches up as cards move");
            return;
        }
        foreach (var holder in view.ActiveHolders.ToList())
        {
            view.RemoveCardHolder(holder);
        }
        for (int i = 0; i < hand.Cards.Count; i++)
        {
            var node = NCard.Create(hand.Cards[i]);
            if (node != null)
            {
                view.Add(node, i);
            }
        }
        view.ForceRefreshCardIndices();
        result.Fix($"{who} hand view", $"rebuilt from the Hand pile ({hand.Cards.Count} cards)");
    }

    /// <summary>The card-ID side of one resync: the layouts it works from and every player's planned pile rebuild.</summary>
    private sealed class IdPlan
    {
        public IdPlan(CardIdLayout? host, CardIdLayout? baseIds, CardIdLayout now, uint? nowNext)
        {
            Host = host;
            Base = baseIds;
            Now = now;
            NowNext = nowNext;
        }

        public CardIdLayout? Host { get; }
        public CardIdLayout? Base { get; }
        public CardIdLayout Now { get; }
        public uint? NowNext { get; }
        public List<PilePlan> Piles { get; } = new();
    }

    /// <summary>One player's piles as the host has them: per slot, our card that takes it (null: create one).</summary>
    private sealed class PilePlan
    {
        public PilePlan(Player player, PlayerCombatState combat, ICombatState scope, NetFullCombatState.PlayerState host,
            string who, List<CardPile> piles)
        {
            Player = player;
            Combat = combat;
            Scope = scope;
            Host = host;
            Who = who;
            Piles = piles;
        }

        public Player Player { get; }
        public PlayerCombatState Combat { get; }
        public ICombatState Scope { get; }
        public NetFullCombatState.PlayerState Host { get; }
        public string Who { get; }
        public List<CardPile> Piles { get; }
        public List<(CardPile pile, CardModel? card, NetFullCombatState.CardState state, uint id)> Slots { get; } = new();
        public List<CardModel> Leftovers { get; } = new();
        public int ById { get; set; }
        public int ByValue { get; set; }
    }

    // ------------------------------------------------------------- creatures

    private static void ApplyCreatures(RunState run, NetFullCombatState host, NetFullCombatState? baseState, bool turnMoved, ResyncResult result)
    {
        var combatState = run.Players[0].Creature.CombatState;
        var local = combatState?.Creatures;
        if (local == null || host.Creatures.Count == 0)
        {
            if (host.Creatures.Count > 0)
            {
                result.Skip("creatures", "host has creatures, we have no combat");
            }
            return;
        }
        if (local.Count != host.Creatures.Count)
        {
            result.Skip("creatures", $"count differs ({local.Count} local vs {host.Creatures.Count} host); creature set is not overridable");
            return;
        }

        for (int i = 0; i < local.Count; i++)
        {
            var creature = local[i];
            var h = host.Creatures[i];
            if (creature.Monster?.Id != h.monsterId || creature.Player?.NetId != h.playerId)
            {
                result.Skip($"creature[{i}]", $"identity differs ({creature.LogName} vs {h.monsterId?.ToString() ?? h.playerId?.ToString()})");
                continue;
            }
            NetFullCombatState.CreatureState? b = baseState != null && baseState.Creatures.Count == local.Count ? baseState.Creatures[i] : null;
            ApplyCreature(creature, h, b, turnMoved, result);
        }
    }

    private static void ApplyCreature(Creature creature, NetFullCombatState.CreatureState host, NetFullCombatState.CreatureState? b, bool turnMoved, ResyncResult result)
    {
        string who = $"creature {creature.LogName}";

        if (SetMaxHp != null)
        {
            ApplyScalar($"{who} max hp", host.maxHp, b?.maxHp, creature.MaxHp, 1, turnMoved, v => SetMaxHp(creature, v), result);
        }
        if (SetCurrentHp != null)
        {
            ApplyScalar($"{who} hp", host.currentHp, b?.currentHp, creature.CurrentHp, 0, turnMoved, v => SetCurrentHp(creature, v), result,
                max: Math.Max(1, creature.MaxHp));
        }
        if (SetBlock != null)
        {
            ApplyScalar($"{who} block", host.block, b?.block, creature.Block, 0, turnMoved, v => SetBlock(creature, v), result);
        }

        var localPowers = creature.Powers.Select(p => new NetFullCombatState.PowerState { id = p.Id, amount = p.Amount }).ToList();
        if (ListBytesEqual(localPowers, host.powers))
        {
            return;
        }

        // Powers are matched one by one in the host's order, so two instances of the same power (instanced powers) keep
        // their own amounts instead of both mapping onto the first. The checksum hashes them in order, and both
        // ApplyInternal and a removal change the order, so they are put back in the host's order at the end.
        var localIds = creature.Powers.Select(p => p.Id).ToList();
        var hostIds = host.powers.Select(p => p.id).ToList();
        var changes = new List<string>();
        if (Same(localIds, hostIds))
        {
            // Same powers: merge the amounts.
            var pool = creature.Powers.ToList();
            var seen = new Dictionary<ModelId, int>();
            foreach (var hp in host.powers)
            {
                var power = pool.First(p => p.Id == hp.id);
                pool.Remove(power);
                int nth = seen.TryGetValue(hp.id, out var n) ? n : 0;
                seen[hp.id] = nth + 1;
                var basePowers = b?.powers.Where(bp => bp.id == hp.id).ToList();
                int? baseAmount = b == null ? null : nth < basePowers!.Count ? basePowers[nth].amount : null;
                var outcome = MergeScalar(hp.amount, baseAmount, power.Amount, turnMoved, out int target);
                if (outcome == Merged.Defer)
                {
                    result.Skip($"{who} power {power.Id}", DeferReason);
                }
                else if (outcome == Merged.Write && target != power.Amount)
                {
                    changes.Add($"{power.Id} {power.Amount} -> {target}");
                    power.SetAmount(target, silent: true);
                }
            }
        }
        else
        {
            if (b != null && !ListBytesEqual(localPowers, b.Value.powers))
            {
                result.Skip($"{who} power set", "changed locally since checkpoint");
                return;
            }

            var pool = creature.Powers.ToList();
            var missing = new List<NetFullCombatState.PowerState>();
            foreach (var hp in host.powers)
            {
                var existing = pool.FirstOrDefault(p => p.Id == hp.id);
                if (existing == null)
                {
                    missing.Add(hp);
                    continue;
                }
                pool.Remove(existing);
                if (existing.Amount != hp.amount)
                {
                    existing.SetAmount(hp.amount, silent: true);
                    changes.Add($"{hp.id} = {hp.amount}");
                }
            }
            foreach (var power in pool)
            {
                power.RemoveInternal();
                changes.Add($"-{power.Id}");
            }
            foreach (var hp in missing)
            {
                var canonical = ModelDb.GetByIdOrNull<PowerModel>(hp.id);
                if (canonical == null)
                {
                    result.Skip($"{who} power {hp.id}", "unknown power id");
                    continue;
                }
                canonical.ToMutable().ApplyInternal(creature, hp.amount, silent: true);
                changes.Add($"+{hp.id} {hp.amount}");
            }
        }
        if (Reorder<PowerModel>(PowersField, creature, hostIds, p => p.Id))
        {
            changes.Add("reordered");
        }
        if (changes.Count > 0)
        {
            result.Fix($"{who} powers", string.Join(", ", changes));
        }
    }

    // --------------------------------------------------------------- helpers

    private enum Merged { Keep, Write, Defer }

    private const string DeferReason =
        "changed since the checkpoint while the turn moved on, so a turn-start reset may be in it; left for the next checkpoint";

    /// <summary>
    /// Three-way merge of one counter: <paramref name="host"/> and <paramref name="b"/> are the host's and our own value
    /// at the checkpoint, <paramref name="now"/> is ours now.
    /// </summary>
    /// <remarks>
    /// In step at the checkpoint: whatever happened since is our own, legitimately (<see cref="Merged.Keep"/>).
    /// Untouched since the checkpoint: take the host's value. Otherwise re-apply our change on top of the host's,
    /// <c>host + (now - base)</c>, except when the turn moved on, where the change may be an outright reset (energy
    /// refilled to its maximum, block cleared) that the delta would apply twice: then wait for the next checkpoint.
    /// </remarks>
    private static Merged MergeScalar(int host, int? b, int now, bool turnMoved, out int target)
    {
        target = now;
        if (b == null)
        {
            target = host;
            return target == now ? Merged.Keep : Merged.Write;
        }
        if (b.Value == host)
        {
            return Merged.Keep;
        }
        if (now == b.Value)
        {
            target = host;
            return Merged.Write;
        }
        if (turnMoved)
        {
            return Merged.Defer;
        }
        target = host + (now - b.Value);
        return target == now ? Merged.Keep : Merged.Write;
    }

    /// <summary><see cref="MergeScalar"/> for one field, clamped, written through <paramref name="set"/> and reported.</summary>
    private static void ApplyScalar(string what, int host, int? b, int now, int min, bool turnMoved, Action<int> set,
        ResyncResult result, int max = int.MaxValue)
    {
        switch (MergeScalar(host, b, now, turnMoved, out int target))
        {
            case Merged.Keep:
                return;
            case Merged.Defer:
                result.Skip(what, DeferReason);
                return;
        }
        target = Math.Clamp(target, min, max);
        if (target == now)
        {
            return;
        }
        set(target);
        result.Fix(what, $"{now} -> {target}");
    }

    private static NetFullCombatState.PlayerState? FindPlayer(NetFullCombatState? state, ulong id)
    {
        if (state == null)
        {
            return null;
        }
        foreach (var p in state.Players)
        {
            if (p.playerId == id)
            {
                return p;
            }
        }
        return null;
    }

    private static bool Same<T>(List<T> a, List<T> b) => a.Count == b.Count && a.OrderBy(x => x?.ToString()).SequenceEqual(b.OrderBy(x => x?.ToString()));

    public static bool BytesEqual<T>(T a, T b) where T : IPacketSerializable => Bytes(a).AsSpan().SequenceEqual(Bytes(b));

    public static bool ListBytesEqual<T>(IReadOnlyList<T> a, IReadOnlyList<T> b) where T : IPacketSerializable
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (!BytesEqual(a[i], b[i]))
            {
                return false;
            }
        }
        return true;
    }

    private static byte[] Bytes<T>(T value) where T : IPacketSerializable
    {
        Writer.Reset();
        Writer.Write(value);
        Writer.ZeroByteRemainder();
        return Writer.Buffer.AsSpan(0, Writer.BytePosition).ToArray();
    }

    private static string Join<T>(IEnumerable<T> items) => string.Join(",", items);

    private static Action<TObj, TVal>? MakeSetter<TObj, TVal>(string property)
    {
        var setter = AccessTools.PropertySetter(typeof(TObj), property);
        if (setter == null)
        {
            MainFile.Logger.Warn($"{typeof(TObj).Name}.{property} has no setter; that field will not be resynced");
            return null;
        }
        return (Action<TObj, TVal>)Delegate.CreateDelegate(typeof(Action<TObj, TVal>), setter);
    }

    private static void Try(ResyncResult result, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            result.Fail(what, e);
        }
    }
}

public sealed class ResyncResult
{
    private readonly List<string> _fixed = new();
    private readonly List<string> _skipped = new();
    private readonly List<string> _failed = new();

    public ResyncResult(uint checksumId, bool hadBaseline)
    {
        ChecksumId = checksumId;
        HadBaseline = hadBaseline;
    }

    public uint ChecksumId { get; }
    public bool HadBaseline { get; }
    public int FixedCount => _fixed.Count;

    public void Fix(string what, string detail) => _fixed.Add($"{what}: {detail}");
    public void Skip(string what, string why) => _skipped.Add($"{what}: {why}");

    public void Fail(string what, Exception e)
    {
        _failed.Add($"{what}: {e.GetType().Name} {e.Message}");
        MainFile.Logger.Warn($"Resync of {what} failed: {e}");
    }

    public string ToLogString()
    {
        var sb = new StringBuilder();
        sb.Append($"[HostStateResync] checksum #{ChecksumId}: applied {_fixed.Count} fix(es), skipped {_skipped.Count}, failed {_failed.Count}");
        sb.Append(HadBaseline ? " (three-way merge against our own checkpoint snapshot)" : " (no local checkpoint snapshot; host values applied as-is)");
        foreach (var f in _fixed) sb.Append("\n  fixed   ").Append(f);
        foreach (var s in _skipped) sb.Append("\n  skipped ").Append(s);
        foreach (var f in _failed) sb.Append("\n  FAILED  ").Append(f);
        return sb.ToString();
    }

    public string ToScreenString()
    {
        if (_fixed.Count == 0 && _skipped.Count == 0 && _failed.Count == 0)
        {
            return $"Desync #{ChecksumId}: nothing visible differed from the host";
        }
        var parts = new List<string>();
        if (_fixed.Count > 0) parts.Add($"{_fixed.Count} fixed");
        if (_skipped.Count > 0) parts.Add($"{_skipped.Count} left alone");
        if (_failed.Count > 0) parts.Add($"{_failed.Count} failed");
        return $"Desync #{ChecksumId}: resynced from host ({string.Join(", ", parts)})";
    }
}
