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
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Entities.Orbs;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Orbs;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace SlayTheDivergence.Resync;

/// <summary>
/// Writes the host's <see cref="NetFullCombatState"/> snapshot into the client's live run state.
///
/// The snapshot was taken by the host at checksum N. By the time it arrives, the client may already have executed
/// more actions after N (the game is lockstep: both peers run the same action sequence). So the merge is three-way:
/// <c>baseState</c> is the client's own snapshot at N (kept by <c>ChecksumTracker</c> for the last 20 checksums),
/// <c>hostState</c> is the host's snapshot at N, and the live objects are "now".
/// <list type="bullet">
/// <item>Anything that matched the host at N is left alone: whatever happened to it since is our own.</item>
/// <item>Counters (HP, block, energy, gold, power amounts): see <see cref="MergeScalar"/>.</item>
/// <item>RNG streams and sync markers: loaded from the host and advanced by the local draws since N.</item>
/// <item>Collections (piles, potions, relics, power set, orbs): replaced by the host's version only if they have not
/// changed locally since N, and kept in the host's order. Otherwise the stale snapshot would destroy legitimate local
/// progress; the next checkpoint delivers a fresher snapshot if they still differ.</item>
/// <item>Card piles and card IDs: planned for every player, validated, then applied together (<see cref="CommitPiles"/>).</item>
/// </list>
/// Writes go through the game's own mutators with <c>silent: true</c>, private setters and private fields: no gameplay
/// hooks or VFX fire (a few setters still raise their change events), and most UI is not notified; the hand view is
/// synced explicitly (<see cref="SyncHandView"/>).
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
        // Once a player's turn or phase moved on since the checkpoint, those fields (energy, stars, block, power
        // amounts) may hold a reset, which neither a delta nor "untouched since the checkpoint" can tell apart.
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
            if (run.GetPlayer(hostPlayer.playerId) is not { PlayerCombatState: { } combat } orbOwner) continue;
            var hp = hostPlayer;
            var basePlayer = FindPlayer(baseState, hostPlayer.playerId);
            Try(result, $"player {hp.playerId} orbs", () => ApplyOrbs(orbOwner, combat, hp, basePlayer, $"player {hp.playerId}", result));
        }
        // After the creatures: card values (RecalculateCardValues) follow the powers just written.
        Try(result, "card piles", () => CommitPiles(ids, run, result));
        // Last: ending the combat runs most of its teardown synchronously (powers, block and piles cleared), which must
        // not happen under the repair's feet.
        if (result.NeedsWinCheck)
        {
            TaskHelper.RunSafely(CombatManager.Instance.CheckWinCondition());
        }
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

        ApplyScalar($"{who} gold", host.gold, baseP?.gold, player.Gold, 0, false, v => player.Gold = v, result);
        ApplyScalar($"{who} max potions", host.maxPotionCount, baseP?.maxPotionCount, player.MaxPotionCount, 0, false, v =>
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
    /// Puts the host's orbs in the host's order. Orbs of the same type are reused, so whatever other mods keep per orb
    /// instance survives; an orb only the host has is created from its id, and one only we have is removed without
    /// its evoke (the host already applied that). The orb display is then brought in line slot by slot through
    /// <c>NOrb.ReplaceOrb</c>, so a created orb has a node to evoke from later. Passive and evoke values are computed by
    /// hooks (Focus, relics, other mods' per-orb bonuses), so they are not written, with two exceptions: Dark's evoke
    /// value and Glass's passive value are plain fields the orb keeps itself (Dark accumulates, Glass decays). They are
    /// moved by their divergence at the checkpoint (host minus base), which keeps what changed since, or set to the
    /// host's value on an orb created just now.
    /// </summary>
    /// <remarks>
    /// "Changed locally since the checkpoint" compares which orbs there are and their order, not their values: the
    /// values are recomputed from Focus and the like, which the creature repair may just have changed. A created orb
    /// starts without other mods' per-orb state; if that makes its values differ, they are reported as a skip.
    /// </remarks>
    private static void ApplyOrbs(Player player, PlayerCombatState combat, NetFullCombatState.PlayerState host, NetFullCombatState.PlayerState? baseP, string who, ResyncResult result)
    {
        var queue = combat.OrbQueue;
        var localOrbs = queue.Orbs.Select(NetFullCombatState.OrbState.From).ToList();
        if (ListBytesEqual(localOrbs, host.orbs))
        {
            return;
        }
        if (baseP != null && !queue.Orbs.Select(o => o.Id).SequenceEqual(baseP.Value.orbs.Select(o => o.id)))
        {
            result.Skip($"{who} orbs", "changed locally since checkpoint");
            return;
        }
        // Our own value of each orb at the checkpoint (same orbs in the same order, as just checked).
        var baseOf = new Dictionary<OrbModel, NetFullCombatState.OrbState>();
        if (baseP != null)
        {
            for (int i = 0; i < queue.Orbs.Count; i++)
            {
                baseOf[queue.Orbs[i]] = baseP.Value.orbs[i];
            }
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
        if (host.orbs.Count > queue.Capacity)
        {
            result.Skip($"{who} orbs", $"the host has {host.orbs.Count} orbs, we have {queue.Capacity} slots");
            return;
        }

        var changes = new List<string>();
        for (int i = 0; i < desired.Length; i++)
        {
            if (desired[i] != null) continue;
            var orb = ModelDb.GetById<OrbModel>(host.orbs[i].id).ToMutable();
            orb.Owner = player;
            desired[i] = orb;
            changes.Add($"+{orb.Id}");
        }
        foreach (var extra in pool)
        {
            queue.Remove(extra);
            extra.RemoveInternal();
            changes.Add($"-{extra.Id}");
        }

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
            if (changes.Count == 0) changes.Add("reordered");
        }
        if (changes.Count > 0)
        {
            Try(result, $"{who} orb display", () => SyncOrbView(player, queue));
        }
        for (int i = 0; i < order.Count; i++)
        {
            // The two orbs that keep their own number: Dark accumulates its evoke value, Glass loses passive value each
            // turn. Both are moved by their drift at the checkpoint, or to the host's value for an orb created just now.
            var orb = order[i];
            bool known = baseOf.TryGetValue(orb, out var b);
            if (orb is DarkOrb)
            {
                int drift = known ? host.orbs[i].evoke - b.evoke : host.orbs[i].evoke - (int)orb.EvokeVal;
                if (drift != 0 && Nudge(orb, "_evokeVal", drift)) changes.Add($"{orb.Id} evoke {drift:+0;-0}");
            }
            else if (orb is GlassOrb)
            {
                // PassiveVal is _passiveVal plus Focus, clamped at 0 (FocusPower: Math.Max), so the computed drift equals
                // the raw drift until both sides clamp; clamped values hash equal, and the drift is moved once it shows.
                int drift = known ? host.orbs[i].passive - b.passive : host.orbs[i].passive - (int)orb.PassiveVal;
                if (drift != 0 && Nudge(orb, "_passiveVal", drift)) changes.Add($"{orb.Id} passive {drift:+0;-0}");
            }
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

    private static readonly FieldInfo? OrbNodesField = AccessTools.Field(typeof(NOrbManager), "_orbs");

    /// <summary>
    /// Shows the queue's orbs in the queue's order: one slot node per orb slot, filled front to back, each pointed at its
    /// orb with <c>NOrb.ReplaceOrb</c> (the game's own swap), empty slots last. Without this a created orb has no node,
    /// and the next evoke throws looking it up.
    /// </summary>
    public static void SyncOrbView(Player player, OrbQueue queue)
    {
        var manager = NCombatRoom.Instance?.GetCreatureNode(player.Creature)?.OrbManager;
        if (manager == null || OrbNodesField?.GetValue(manager) is not List<NOrb> nodes)
        {
            return;
        }
        for (int i = 0; i < nodes.Count; i++)
        {
            var want = i < queue.Orbs.Count ? queue.Orbs[i] : null;
            if (nodes[i].Model != want)
            {
                // ReplaceOrb takes null for an empty slot: it sets Model and redraws, and UpdateVisuals draws null as empty.
                nodes[i].ReplaceOrb(want!);
            }
        }
    }

    /// <summary>Whether every slot node shows the queue's orb at that slot (and the rest are empty). True without a display.</summary>
    public static bool OrbViewMatches(Player player)
    {
        var queue = player.PlayerCombatState?.OrbQueue;
        var manager = NCombatRoom.Instance?.GetCreatureNode(player.Creature)?.OrbManager;
        if (queue == null || manager == null || OrbNodesField?.GetValue(manager) is not List<NOrb> nodes)
        {
            return true;
        }
        return nodes.Count >= queue.Orbs.Count &&
               nodes.Select((node, i) => node.Model == (i < queue.Orbs.Count ? queue.Orbs[i] : null)).All(ok => ok);
    }

    /// <summary>Moves a decimal field the orb type itself declares by <paramref name="delta"/>.</summary>
    private static bool Nudge(OrbModel orb, string field, int delta)
    {
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

        // Create every missing card first. That is the only step here that can plausibly throw (deserializing a card),
        // and if it does, nothing has changed yet except the cards just registered, which are taken out again.
        var keyed = new List<(CardModel card, uint id)>();
        var unkeyed = new List<CardModel>();
        var placedByPlan = new List<List<(CardPile pile, CardModel card)>>();
        var registered = new List<(PilePlan plan, CardModel card)>();
        try
        {
            foreach (var plan in ids.Piles)
            {
                var placed = new List<(CardPile pile, CardModel card)>();
                foreach (var (pile, card, state, id) in plan.Slots)
                {
                    var placedCard = card;
                    if (placedCard == null)
                    {
                        placedCard = CardModel.FromSerializable(state.card);
                        // The combat scope, as CombatState.CreateCard does: CardPileCmd.Add and manual plays throw for
                        // an in-combat card the CombatState does not contain, so a run-scoped card would break on its
                        // next draw.
                        plan.Scope.AddCard(placedCard, plan.Player);
                        registered.Add((plan, placedCard));
                        if (id != CardIdLayout.NoId)
                        {
                            keyed.Add((placedCard, id));
                        }
                        else
                        {
                            unkeyed.Add(placedCard);
                        }
                    }
                    placed.Add((pile, placedCard));
                }
                placedByPlan.Add(placed);
            }
        }
        catch
        {
            foreach (var (plan, card) in registered)
            {
                plan.Scope.RemoveCard(card);
            }
            foreach (var plan in ids.Piles)
            {
                result.Skip($"{plan.Who} card piles", "not rebuilt: a card could not be created");
            }
            throw;
        }

        // Then the piles and the IDs, with nothing in between that calls out of this class.
        for (int p = 0; p < ids.Piles.Count; p++)
        {
            var plan = ids.Piles[p];
            foreach (var pile in plan.Piles)
            {
                foreach (var card in pile.Cards.ToList())
                {
                    pile.RemoveInternal(card, silent: true);
                }
            }
            foreach (var (pile, card) in placedByPlan[p])
            {
                pile.AddInternal(card, -1, silent: true);
            }
        }
        if (rewrite != null)
        {
            int changed = CardIdDb.Commit(rewrite, keyed, unkeyed);
            uint after = CardIdDb.NextId ?? nowNext;
            if (changed > 0 || rewrite.Evicted > 0 || after != nowNext)
            {
                string evicted = rewrite.Evicted > 0 ? $", {rewrite.Evicted} taken from cards no longer in play" : "";
                result.Fix("card ids", $"{changed} card(s) re-keyed to the host's IDs{evicted}, next ID {nowNext} -> {after}");
            }
        }

        // Only now tell the piles' listeners (NetCombatCardDb, the UI). The IDs are in place, so a listener that throws
        // cannot leave rebuilt piles with misplaced IDs behind; each pile gets its own guard.
        foreach (var plan in ids.Piles)
        {
            foreach (var pile in plan.Piles)
            {
                Try(result, $"{plan.Who} {pile.Type} pile events", pile.InvokeContentsChanged);
            }
        }

        foreach (var plan in ids.Piles)
        {
            plan.Combat.RecalculateCardValues();
            int createdHere = registered.Count(r => r.plan == plan);
            string matched = ids.Host != null ? $"{plan.ById} by card ID, {plan.ByValue} by value, " : "";
            result.Fix($"{plan.Who} card piles",
                $"rebuilt from host ({matched}{createdHere} cards created, {plan.Leftovers.Count} removed)");

            // Nothing here writes a card's own values, so a card whose cost, keywords, affliction or enchantment differ
            // from every candidate stays different. Say so instead of letting the rebuild read as a full repair.
            var afterPiles = plan.Piles.Select(NetFullCombatState.CombatPileState.From).ToList();
            int stillDifferent = afterPiles.Zip(plan.Host.piles).Sum(x => x.First.cards.Zip(x.Second.cards).Count(c => !BytesEqual(c.First, c.Second)));
            if (stillDifferent > 0)
            {
                result.Skip($"{plan.Who} card piles", $"{stillDifferent} card(s) still differ in cost, keywords, affliction or enchantment, which a resync cannot write");
            }
        }

        // The hand view last, on its own: the game state is complete by now, and a UI failure must not undo or skip it.
        foreach (var plan in ids.Piles.Where(p => LocalContext.IsMe(p.Player)))
        {
            Try(result, $"{plan.Who} hand view", () => SyncHandView(plan.Combat.Hand, plan.Who, result));
        }
    }

    /// <summary>
    /// NPlayerHand builds its card nodes from the pile commands, not from pile events, so after a silent rebuild it can
    /// show cards that left the hand (clickable, and their play is cancelled on both peers) and miss cards that are now
    /// in it (invisible and unplayable). Bring the view in line with the Hand pile: drop the holders whose card left and
    /// add one for each hand card that has none. <see cref="NPlayerHand.GetCardHolder"/> also finds the holders of
    /// selected cards and of plays waiting in the queue, whose cards stay in the Hand pile until the play runs, so those
    /// are left alone rather than duplicated.
    /// </summary>
    private static void SyncHandView(CardPile hand, string who, ResyncResult result)
    {
        var view = NPlayerHand.Instance;
        if (view == null)
        {
            return;
        }
        if (view.InCardPlay || view.IsInCardSelection)
        {
            result.Skip($"{who} hand view", "a card is being played or selected; the view is left as it is");
            return;
        }

        int removed = 0, added = 0;
        foreach (var holder in view.ActiveHolders.ToList())
        {
            var node = holder.CardNode;
            if (node?.Model == null || hand.Cards.Contains(node.Model))
            {
                continue;
            }
            // RemoveCardHolder detaches the card node without freeing it; hand it back to the node pool.
            view.RemoveCardHolder(holder);
            node.QueueFreeSafely();
            removed++;
        }
        // A new card goes where the pile has it, counted among the holders shown, as GetHandInsertIndex does. The order
        // of cards already shown is not changed.
        var position = hand.Cards.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        foreach (var card in hand.Cards)
        {
            if (view.GetCardHolder(card) != null)
            {
                continue;
            }
            var node = NCard.Create(card);
            if (node == null)
            {
                continue;
            }
            node.UpdateVisuals(PileType.Hand, CardPreviewMode.Normal);
            int index = view.ActiveHolders.Count(h => h.CardNode?.Model is { } shown && position.TryGetValue(shown, out var at) && at < position[card]);
            view.Add(node, index);
            added++;
        }
        if (removed + added > 0)
        {
            view.ForceRefreshCardIndices();
            result.Fix($"{who} hand view", $"{removed} stale card(s) removed, {added} added");
        }
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
        // Removing a creature changes the live list, so work on a copy.
        var creatures = local.ToList();
        var localKeys = creatures.Select(c => new CreatureMatch.Key(c.Monster?.Id, c.Player?.NetId, c.CurrentHp)).ToList();
        var toHost = CreatureMatch.Align(Keys(host), localKeys);
        if (toHost == null)
        {
            result.Skip("creatures", $"the host has creatures we don't ({creatures.Count} local vs {host.Creatures.Count} host); " +
                                     "creating creatures is not repaired");
            return;
        }
        var toBase = baseState != null ? CreatureMatch.Align(Keys(baseState), localKeys, partial: true) : null;

        var extras = Enumerable.Range(0, creatures.Count).Where(i => toHost[i] < 0).ToList();
        if (extras.Count > 0 && combatState != null)
        {
            RemoveGoneCreatures(combatState, extras.Select(i => creatures[i]).ToList(), host, baseState, result);
        }

        for (int i = 0; i < creatures.Count; i++)
        {
            if (toHost[i] < 0) continue;
            NetFullCombatState.CreatureState? b = toBase != null && toBase[i] >= 0 ? baseState!.Creatures[toBase[i]] : null;
            ApplyCreature(creatures[i], host.Creatures[toHost[i]], b, turnMoved, result);
        }
    }

    private static List<CreatureMatch.Key> Keys(NetFullCombatState state) =>
        state.Creatures.Select(c => new CreatureMatch.Key(c.monsterId, c.playerId, c.currentHp)).ToList();

    /// <summary>
    /// Removes enemies the host no longer has, the way a death removes them, but without the death's hooks: those ran
    /// on the host, and their results (powers, HP, relic counters) arrive with the rest of its state. Gated like every
    /// other repair: only an enemy we already had more of than the host at the checkpoint goes, never one that spawned
    /// here since, and never an ally or a pet.
    /// </summary>
    private static void RemoveGoneCreatures(ICombatState combatState, List<Creature> extras, NetFullCombatState host,
        NetFullCombatState? baseState, ResyncResult result)
    {
        if (baseState == null)
        {
            result.Skip("creatures", $"{extras.Count} creature(s) the host doesn't have, but no checkpoint snapshot to tell why");
            return;
        }
        // A monster takes its turn after waits during which it is not yet marked as moving; removing it then would let its
        // move run anyway. The divergence repeats at the next checkpoint, so the removal just waits for our own turn.
        if (combatState.CurrentSide == CombatSide.Enemy)
        {
            result.Skip("creatures", $"{extras.Count} creature(s) the host doesn't have; left for the next checkpoint, it is the enemies' turn");
            return;
        }
        // How many of each monster the host had removed by the checkpoint and we had not.
        var budget = baseState.Creatures.Where(c => c.monsterId != null).GroupBy(c => c.monsterId!)
            .ToDictionary(g => g.Key, g => g.Count() - host.Creatures.Count(h => h.monsterId == g.Key));

        bool removedAny = false;
        foreach (var creature in extras)
        {
            string who = $"creature {creature.LogName}";
            var id = creature.Monster?.Id;
            if (!creature.IsMonster || !creature.IsEnemy || id == null)
            {
                result.Skip(who, "the host doesn't have it, but only enemies are removed");
                continue;
            }
            if (!budget.TryGetValue(id, out int left) || left <= 0)
            {
                result.Skip(who, "the host doesn't have it, but it appeared here after the checkpoint");
                continue;
            }
            if (creature.Monster!.IsPerformingMove)
            {
                result.Skip(who, "the host doesn't have it, but it is in the middle of its move");
                continue;
            }
            budget[id] = left - 1;
            try
            {
                RemoveCreature(combatState, creature);
                removedAny = true;
                result.Fix($"{who} removed", "already gone in the host's game");
            }
            catch (Exception e)
            {
                result.Fail($"{who} removal", e);
            }
        }

        // The last enemy gone ends the combat on the host; without this check the client would wait in an empty room.
        // Apply runs it once everything else is written.
        if (removedAny && !combatState.Enemies.Any(e => e.IsAlive))
        {
            result.NeedsWinCheck = true;
        }
    }

    /// <summary>The removal half of <c>CreatureCmd.KillWithoutCheckingWinCondition</c>, with the hooks left out.</summary>
    public static void RemoveCreature(ICombatState combatState, Creature creature)
    {
        if (creature.IsAlive && SetCurrentHp != null)
        {
            SetCurrentHp(creature, 0);
        }
        creature.RemoveAllPowersInternalExcept();
        var node = NCombatRoom.Instance?.GetCreatureNode(creature);
        if (node != null)
        {
            node.StartDeathAnim(shouldRemove: true);
            NCombatRoom.Instance!.RemoveCreatureNode(node);
        }
        CombatManager.Instance.RemoveCreature(creature);
        combatState.RemoveCreature(creature);
    }

    private static void ApplyCreature(Creature creature, NetFullCombatState.CreatureState host, NetFullCombatState.CreatureState? b, bool turnMoved, ResyncResult result)
    {
        string who = $"creature {creature.LogName}";

        if (SetMaxHp != null)
        {
            ApplyScalar($"{who} max hp", host.maxHp, b?.maxHp, creature.MaxHp, 1, false, v => SetMaxHp(creature, v), result);
        }
        if (SetCurrentHp != null)
        {
            ApplyScalar($"{who} hp", host.currentHp, b?.currentHp, creature.CurrentHp, 0, false, v => SetCurrentHp(creature, v), result,
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
                if (hp.amount == 0)
                {
                    result.Skip($"{who} power {hp.id}", "the host has it at amount 0, which ApplyInternal does not add");
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
        "the turn moved on since the checkpoint and turn setup may have reset it; left for the next checkpoint";

    /// <summary>
    /// Three-way merge of one counter: <paramref name="host"/> and <paramref name="b"/> are the host's and our own value
    /// at the checkpoint, <paramref name="now"/> is ours now.
    /// </summary>
    /// <remarks>
    /// In step at the checkpoint: whatever happened since is our own, legitimately (<see cref="Merged.Keep"/>).
    /// <paramref name="mayHaveReset"/> (the turn moved on and turn setup assigns this field outright: energy refilled
    /// to its maximum, block cleared) means neither the value nor its change since the checkpoint can be trusted, even
    /// when it looks untouched, since a reset can land on the old value: leave it to the next checkpoint unless it
    /// already equals the host's. Otherwise: untouched since the checkpoint, take the host's value; changed, re-apply
    /// our change on top of the host's, <c>host + (now - base)</c>.
    /// </remarks>
    private static Merged MergeScalar(int host, int? b, int now, bool mayHaveReset, out int target)
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
        if (mayHaveReset)
        {
            return now == host ? Merged.Keep : Merged.Defer;
        }
        if (now == b.Value)
        {
            target = host;
            return Merged.Write;
        }
        target = host + (now - b.Value);
        return target == now ? Merged.Keep : Merged.Write;
    }

    /// <summary><see cref="MergeScalar"/> for one field, clamped, written through <paramref name="set"/> and reported.</summary>
    private static void ApplyScalar(string what, int host, int? b, int now, int min, bool mayHaveReset, Action<int> set,
        ResyncResult result, int max = int.MaxValue)
    {
        switch (MergeScalar(host, b, now, mayHaveReset, out int target))
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

    /// <summary>An enemy removal left no living enemy, so the combat may be over; <see cref="HostStateApplier.Apply"/> checks last.</summary>
    public bool NeedsWinCheck { get; set; }

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
        sb.Append($"[SlayTheDivergence] checksum #{ChecksumId}: applied {_fixed.Count} fix(es), skipped {_skipped.Count}, failed {_failed.Count}");
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
