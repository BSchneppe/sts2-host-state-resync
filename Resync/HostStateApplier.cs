using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
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
        Try(result, "card ids", () => ApplyCardIds(ids, result));
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
            ApplyPiles(player, combat, host, baseP, ids, who, result);
        }

        ApplyOrbs(player, combat, host, baseP, who, result);
    }

    private static void ApplyPotions(Player player, NetFullCombatState.PlayerState host, NetFullCombatState.PlayerState? baseP, string who, ResyncResult result)
    {
        var localIds = player.Potions.Select(p => p.Id).ToList();
        var hostIds = host.potions.Select(p => p.id).ToList();
        if (Same(localIds, hostIds))
        {
            return;
        }
        if (baseP != null && !Same(localIds, baseP.Value.potions.Select(p => p.id).ToList()))
        {
            result.Skip($"{who} potions", "changed locally since checkpoint");
            return;
        }

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
        result.Fix($"{who} potions", $"removed [{Join(extra)}], added [{Join(missing)}]");
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
        if (changes.Count > 0)
        {
            result.Fix($"{who} relics", string.Join(", ", changes));
        }
    }

    // ------------------------------------------------------------------ orbs

    /// <summary>
    /// Orb identity and order are rebuilt from the host (existing instances reused by id, missing ones created from
    /// ModelDb). Passive/evoke values are derived from powers such as Focus, so they are not written; the only stateful
    /// exception is Dark, whose accumulated evoke value lives in a private field that is set to the host value.
    /// </summary>
    private static void ApplyOrbs(Player player, PlayerCombatState combat, NetFullCombatState.PlayerState host, NetFullCombatState.PlayerState? baseP, string who, ResyncResult result)
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
        if (host.orbs.Count > queue.Capacity)
        {
            result.Skip($"{who} orbs", $"host has {host.orbs.Count} orbs but our capacity is {queue.Capacity}");
            return;
        }

        var pool = queue.Orbs.ToList();
        var desired = new List<OrbModel>();
        int created = 0;
        foreach (var hostOrb in host.orbs)
        {
            int idx = pool.FindIndex(o => o.Id == hostOrb.id);
            OrbModel orb;
            if (idx >= 0)
            {
                orb = pool[idx];
                pool.RemoveAt(idx);
            }
            else
            {
                var canonical = ModelDb.GetByIdOrNull<OrbModel>(hostOrb.id);
                if (canonical == null)
                {
                    result.Skip($"{who} orb {hostOrb.id}", "unknown orb id");
                    continue;
                }
                orb = canonical.ToMutable();
                orb.Owner = player;
                created++;
            }
            if ((int)orb.EvokeVal != hostOrb.evoke)
            {
                var evokeField = AccessTools.Field(orb.GetType(), "_evokeVal");
                if (evokeField != null && evokeField.FieldType == typeof(decimal))
                {
                    evokeField.SetValue(orb, (decimal)hostOrb.evoke);
                }
            }
            desired.Add(orb);
        }

        foreach (var orb in queue.Orbs.ToList())
        {
            queue.Remove(orb);
        }
        for (int i = 0; i < desired.Count; i++)
        {
            queue.Insert(i, desired[i]);
        }
        result.Fix($"{who} orbs", $"rebuilt from host ({created} created, {pool.Count} removed)");
    }

    // ----------------------------------------------------------------- piles

    private static void ApplyPiles(Player player, PlayerCombatState combat, NetFullCombatState.PlayerState host, NetFullCombatState.PlayerState? baseP, IdPlan ids, string who, ResyncResult result)
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
        // host's ID, the same card and upgrade. Whatever takes a slot under another ID is re-keyed in ApplyCardIds.
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

        var assignments = new List<(CardPile pile, List<CardModel> cards)>();
        int created = 0, byId = 0, byValue = 0;
        for (int i = 0; i < slots.Count; i++)
        {
            var (pile, hostCard, hostId) = slots[i];
            var match = picks[i];
            if (match == null)
            {
                match = CardModel.FromSerializable(hostCard.card);
                // The combat scope, as CombatState.CreateCard does: CardPileCmd.Add and manual plays throw for an
                // in-combat card the CombatState does not contain, so a run-scoped card would break on its next draw.
                combatScope.AddCard(match, player);
                created++;
            }
            else if (Holds(match, hostId))
            {
                byId++;
            }
            else if (hostId != CardIdLayout.NoId)
            {
                byValue++;
            }
            if (hostId != CardIdLayout.NoId)
            {
                ids.Assign.Add((match, hostId));
            }
            if (assignments.Count == 0 || assignments[^1].pile != pile)
            {
                assignments.Add((pile, new List<CardModel>()));
            }
            assignments[^1].cards.Add(match);
        }
        ids.Forget.AddRange(pool);

        int removed = pool.Count;
        foreach (var pile in localPiles)
        {
            foreach (var card in pile.Cards.ToList())
            {
                pile.RemoveInternal(card, silent: true);
            }
        }
        foreach (var (pile, cards) in assignments)
        {
            foreach (var card in cards)
            {
                pile.AddInternal(card, -1, silent: true);
            }
        }
        foreach (var pile in localPiles)
        {
            pile.InvokeContentsChanged();
        }
        combat.RecalculateCardValues();
        string matched = ids.Host != null ? $"{byId} by card ID, {byValue} by value, " : "";
        result.Fix($"{who} card piles", $"rebuilt from host ({matched}{created} cards created, {removed} removed)");

        // Nothing here writes a card's own values, so a card whose cost, keywords, affliction or enchantment differ
        // from every candidate stays different. Say so instead of letting the rebuild read as a full repair.
        var after = localPiles.Select(NetFullCombatState.CombatPileState.From).ToList();
        int stillDifferent = after.Zip(host.piles).Sum(p => p.First.cards.Zip(p.Second.cards).Count(c => !BytesEqual(c.First, c.Second)));
        if (stillDifferent > 0)
        {
            result.Skip($"{who} card piles", $"{stillDifferent} card(s) still differ in cost, keywords, affliction or enchantment, which a resync cannot write");
        }
    }

    private static bool SameCard(CardModel card, SerializableCard wanted) =>
        card.Id == wanted.Id && card.CurrentUpgradeLevel == wanted.CurrentUpgradeLevel;

    /// <summary>
    /// Gives every card the pile rebuild placed the ID the host has at that slot, and brings the ID counter to
    /// <c>host + (now - base)</c>, shifting IDs handed out locally since the checkpoint along with it. Without this, the
    /// next "play card 23" names different cards on the two peers.
    /// </summary>
    private static void ApplyCardIds(IdPlan ids, ResyncResult result)
    {
        if (ids.Host == null || ids.NowNext == null)
        {
            return;
        }
        uint hostNext = ids.Host.Value.nextId;
        uint nowNext = ids.NowNext.Value;
        uint target = ids.Base != null ? (uint)Math.Max(0, (long)hostNext + nowNext - ids.Base.Value.nextId) : Math.Max(hostNext, nowNext);
        long shift = ids.Base != null ? (long)hostNext - ids.Base.Value.nextId : 0;
        uint shiftFrom = ids.Base?.nextId ?? uint.MaxValue;

        var stale = ids.Assign.Where(a => !NetCombatCardDb.Instance.TryGetCardId(a.card, out var id) || id != a.id).ToList();
        if (stale.Count == 0 && ids.Forget.Count == 0 && shift == 0 && target == nowNext)
        {
            return;
        }
        string? clash = CardIdDb.Rewrite(ids.Assign, ids.Forget, shiftFrom, shift, target, out int changed);
        if (clash != null)
        {
            result.Skip("card ids", clash);
            return;
        }
        uint after = CardIdDb.NextId ?? target;
        if (changed > 0 || after != nowNext)
        {
            result.Fix("card ids", $"{changed} card(s) re-keyed to the host's IDs, next ID {nowNext} -> {after}");
        }
    }

    /// <summary>The card-ID side of one resync: the layouts it works from and what the pile rebuild decided.</summary>
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
        public List<(CardModel card, uint id)> Assign { get; } = new();
        public List<CardModel> Forget { get; } = new();
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

        var localIds = creature.Powers.Select(p => p.Id).ToList();
        var hostIds = host.powers.Select(p => p.id).ToList();
        if (Same(localIds, hostIds))
        {
            // Same powers, different amounts.
            var pool = creature.Powers.ToList();
            var changes = new List<string>();
            foreach (var hp in host.powers)
            {
                var power = pool.First(p => p.Id == hp.id);
                pool.Remove(power);
                int? baseAmount = b?.powers.FirstOrDefault(bp => bp.id == hp.id).amount;
                if (b != null && b.Value.powers.All(bp => bp.id != hp.id))
                {
                    baseAmount = null;
                }
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
            if (changes.Count > 0)
            {
                result.Fix($"{who} powers", string.Join(", ", changes));
            }
            return;
        }

        if (b != null && !ListBytesEqual(localPowers, b.Value.powers))
        {
            result.Skip($"{who} power set", "changed locally since checkpoint");
            return;
        }

        var changed = new List<string>();
        foreach (var power in creature.Powers.Where(p => !hostIds.Contains(p.Id)).ToList())
        {
            power.RemoveInternal();
            changed.Add($"-{power.Id}");
        }
        foreach (var hp in host.powers)
        {
            var existing = creature.GetPowerById(hp.id);
            if (existing != null)
            {
                if (existing.Amount != hp.amount)
                {
                    existing.SetAmount(hp.amount, silent: true);
                    changed.Add($"{hp.id} = {hp.amount}");
                }
                continue;
            }
            var canonical = ModelDb.GetByIdOrNull<PowerModel>(hp.id);
            if (canonical == null)
            {
                result.Skip($"{who} power {hp.id}", "unknown power id");
                continue;
            }
            canonical.ToMutable().ApplyInternal(creature, hp.amount, silent: true);
            changed.Add($"+{hp.id} {hp.amount}");
        }
        if (changed.Count > 0)
        {
            result.Fix($"{who} powers", string.Join(", ", changed));
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
