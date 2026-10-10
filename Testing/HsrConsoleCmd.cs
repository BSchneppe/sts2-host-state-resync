#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HostStateResync.Patches;
using HostStateResync.Resync;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace HostStateResync.Testing;

/// <summary>
/// Dev-console command <c>hsr</c>. Discovered automatically by the game's <c>DevConsole</c>, which scans mod assemblies
/// for <see cref="AbstractConsoleCmd"/> subclasses; debug commands are enabled whenever the game runs modded.
///
/// <c>hsr selftest</c>: in a singleplayer combat (it refuses co-op), for each scenario: snapshot the current state as the
/// "host truth", perturb the live state as if this peer had drifted, snapshot that as the peer's own checkpoint,
/// run <see cref="HostStateApplier"/> and check that the live state hashes to the truth again. This is a fake second
/// player: the host snapshot is synthesized instead of received.
///
/// <c>hsr desync &lt;kind&gt;</c>: perturb the live state on THIS peer only (the command is not networked). Run it on
/// the client of a real two-instance session (see tools/localmp.sh); the next checkpoint diverges and the mod has to
/// heal it for real.
///
/// <c>hsr status</c>: host strike counters and the last resync result.
/// </summary>
public class HsrConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "hsr";
    public override string Args => "selftest [scenario] | desync <kind> | list | status";
    public override string Description => "HostStateResync: self-test the resync against a synthetic host snapshot, or drift this peer on purpose.";
    public override bool IsNetworked => false;

    public static ResyncResult? LastResult { get; set; }

    private static readonly Dictionary<string, Action<RunState, Player>> Perturbations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["energy"] = (_, p) => p.PlayerCombatState!.Energy += 3,
        ["gold"] = (_, p) => p.Gold += 37,
        ["rng"] = (run, _) => { for (int i = 0; i < 5; i++) run.Rng.Niche.NextInt(100); },
        ["playerrng"] = (_, p) => { for (int i = 0; i < 3; i++) p.PlayerRng.Transformations.NextInt(100); },
        ["hp"] = (_, p) => FirstEnemy(p).SetCurrentHpInternal(Math.Max(1, FirstEnemy(p).CurrentHp - 7)),
        ["block"] = (_, p) => p.Creature.GainBlockInternal(9),
        ["power"] = (_, p) =>
        {
            var enemy = FirstEnemy(p);
            var existing = enemy.Powers.FirstOrDefault();
            if (existing != null)
            {
                existing.SetAmount(existing.Amount + 2, silent: true);
                return;
            }
            var strength = ModelDb.AllPowers.First(x => x.Id.Entry == "STRENGTH_POWER");
            strength.ToMutable().ApplyInternal(enemy, 2, silent: true);
        },
        ["shuffle"] = (_, p) =>
        {
            var pile = p.PlayerCombatState!.DrawPile.Cards.Count >= 2 ? p.PlayerCombatState.DrawPile : p.PlayerCombatState.Hand;
            var cards = pile.Cards.ToList();
            if (cards.Count < 2)
            {
                throw new Unavailable("need at least 2 cards in draw pile or hand");
            }
            foreach (var c in cards) pile.RemoveInternal(c, silent: true);
            cards.Reverse();
            foreach (var c in cards) pile.AddInternal(c, -1, silent: true);
            pile.InvokeContentsChanged();
        },
        ["card"] = (_, p) =>
        {
            var hand = p.PlayerCombatState!.Hand;
            var canonical = ModelDb.AllCards.First(c => c.Id.Entry == "STRIKE" || c.Id.Entry.StartsWith("STRIKE"));
            var card = canonical.ToMutable();
            p.Creature.CombatState!.AddCard(card, p);
            hand.AddInternal(card, -1, silent: true);
            hand.InvokeContentsChanged();
        },
        ["potion"] = (_, p) =>
        {
            var potion = ModelDb.AllPotions.First().ToMutable();
            var res = p.AddPotionInternal(potion, -1, silent: true);
            if (!res.success) throw new Unavailable("no free potion slot");
        },
        ["relic"] = (_, p) =>
        {
            var owned = p.Relics.Select(r => r.Id).ToHashSet();
            var relic = ModelDb.AllRelics.First(r => !owned.Contains(r.Id)).ToMutable();
            p.AddRelicInternal(relic, -1, silent: true);
        },
        // The first orb moves to the end. A resync only ever reorders orbs: creating or destroying one would lose its
        // UI node and other mods' per-orb state, so a drift that adds or removes orbs could never be healed.
        ["orb"] = (_, p) =>
        {
            var queue = p.PlayerCombatState!.OrbQueue;
            var orbs = queue.Orbs.ToList();
            if (orbs.Count < 2 || orbs.All(o => o.Id == orbs[0].Id))
            {
                throw new Unavailable("need two orbs of different types");
            }
            queue.Remove(orbs[0]);
            queue.Insert(orbs.Count - 1, orbs[0]);
        },
        ["choice"] = (_, _) =>
        {
            var sync = RunManager.Instance.PlayerChoiceSynchronizer;
            sync.FastForwardChoiceIds(sync.ChoiceIds.Select(id => id + 1).ToList());
        },
        // Two identical cards trade places: the checksum cannot see it, but the next play of either names a card that
        // is somewhere else on the host. This is what a value-only pile rebuild used to leave behind.
        ["swapids"] = (_, p) =>
        {
            var slots = p.PlayerCombatState!.AllPiles.Where(x => x.IsCombatPile)
                .SelectMany(pile => pile.Cards.Select((card, index) => (pile, card, index))).ToList();
            var pair = slots.SelectMany((a, i) => slots.Skip(i + 1).Where(b =>
                    b.card.Id == a.card.Id && b.card.CurrentUpgradeLevel == a.card.CurrentUpgradeLevel &&
                    (b.pile != a.pile || b.index != a.index))
                .Select(b => (a, b))).FirstOrDefault();
            if (pair.a.card == null) throw new Unavailable("need two identical cards in combat piles");
            var (x, y) = pair;
            x.pile.RemoveInternal(x.card, silent: true);
            y.pile.RemoveInternal(y.card, silent: true);
            if (x.pile == y.pile)
            {
                // Same pile: re-insert in index order so the earlier slot is filled first.
                x.pile.AddInternal(y.card, x.index, silent: true);
                x.pile.AddInternal(x.card, y.index, silent: true);
            }
            else
            {
                x.pile.AddInternal(y.card, x.index, silent: true);
                y.pile.AddInternal(x.card, y.index, silent: true);
            }
            x.pile.InvokeContentsChanged();
            if (y.pile != x.pile) y.pile.InvokeContentsChanged();
        },
        // A card vanishes from the draw pile (the hand if the draw pile is empty). The resync has to create it again in the
        // combat scope and give it the host's ID, which the ID database still holds for the vanished instance.
        ["losecard"] = (_, p) =>
        {
            var combat = p.PlayerCombatState!;
            var pile = combat.DrawPile.Cards.Count > 0 ? combat.DrawPile : combat.Hand;
            if (pile.Cards.Count == 0) throw new Unavailable("need a card in the draw pile or hand");
            pile.RemoveInternal(pile.Cards[pile.Cards.Count / 2], silent: true);
            pile.InvokeContentsChanged();
        },
        // The first power of a creature with at least two vanishes. Re-adding it appends, so the resync also has to put
        // the powers back in the host's order, which the checksum hashes.
        ["losepower"] = (_, p) =>
        {
            var creature = new[] { p.Creature }
                .Concat(p.Creature.CombatState?.Creatures.Where(c => c.IsEnemy && c.IsAlive) ?? Enumerable.Empty<Creature>())
                .FirstOrDefault(c => c.Powers.Count() >= 2) ?? throw new Unavailable("need a creature with at least two powers");
            creature.Powers.First().RemoveInternal();
        },
    };

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length == 0)
        {
            return new CmdResult(success: false, $"Usage: hsr {Args}");
        }
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "list":
                    return new CmdResult(success: true, "Kinds: " + string.Join(", ", Perturbations.Keys));
                case "status":
                    return new CmdResult(success: true, Status());
                case "desync":
                {
                    if (args.Length < 2 || !Perturbations.TryGetValue(args[1], out var perturb))
                    {
                        return new CmdResult(success: false, "Usage: hsr desync <kind>; kinds: " + string.Join(", ", Perturbations.Keys));
                    }
                    var (run, me) = RequireCombat(issuingPlayer);
                    perturb(run, me);
                    return new CmdResult(success: true, args[1].Equals("swapids", StringComparison.OrdinalIgnoreCase)
                        ? "Swapped two identical cards. The checksum cannot see this, so no checkpoint will diverge; the next play of either card is cancelled on the host, which is the failure this drift reproduces."
                        : $"Drifted local state ({args[1]}). The next checkpoint will diverge from the host.");
                }
                case "selftest":
                {
                    // The self-test rewrites the live state and only puts back what a resync repairs. In co-op a
                    // scenario that fails would leave real drift behind; there, drift one peer with hsr desync instead.
                    if (RunManager.Instance.NetService?.Type is NetGameType.Host or NetGameType.Client)
                    {
                        return new CmdResult(success: false, "hsr selftest only runs in singleplayer; in co-op use hsr desync <kind>.");
                    }
                    var (run, me) = RequireCombat(issuingPlayer);
                    var names = args.Length >= 2 ? new[] { args[1] } : Perturbations.Keys.ToArray();
                    var report = SelfTest(run, me, names, includeDelta: args.Length < 2);
                    MainFile.Logger.Info(report.text);
                    return new CmdResult(report.allPassed, report.text);
                }
                default:
                    return new CmdResult(success: false, $"Unknown subcommand '{args[0]}'. Usage: hsr {Args}");
            }
        }
        catch (Exception e)
        {
            return new CmdResult(success: false, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static readonly string[] Subcommands = { "selftest", "desync", "list", "status" };

    /// <summary>Tab completion: the subcommand first, then the drift kind after <c>desync</c> or <c>selftest</c>.</summary>
    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (args.Length <= 1)
        {
            return CompleteArgument(Subcommands, Array.Empty<string>(), args.FirstOrDefault() ?? "", CompletionType.Subcommand);
        }
        if (args.Length == 2 && (args[0].Equals("desync", StringComparison.OrdinalIgnoreCase) ||
                                 args[0].Equals("selftest", StringComparison.OrdinalIgnoreCase)))
        {
            return CompleteArgument(Perturbations.Keys.OrderBy(k => k), new[] { args[0] }, args[1]);
        }
        return new CompletionResult { Type = CompletionType.Argument, ArgumentContext = CmdName };
    }

    private static (RunState run, Player me) RequireCombat(Player? issuingPlayer)
    {
        var run = RunManager.Instance.DebugOnlyGetState() ?? throw new InvalidOperationException("only works during a run");
        var me = issuingPlayer ?? throw new InvalidOperationException("no issuing player");
        if (me.PlayerCombatState == null || !CombatManager.Instance.IsInProgress)
        {
            throw new InvalidOperationException("only works in combat");
        }
        return (run, me);
    }

    private static Creature FirstEnemy(Player p) =>
        p.Creature.CombatState?.Creatures.FirstOrDefault(c => c.IsEnemy && c.IsAlive)
        ?? throw new Unavailable("no living enemy");

    private static (bool allPassed, string text) SelfTest(RunState run, Player me, IEnumerable<string> names, bool includeDelta)
    {
        var tracker = RunManager.Instance.ChecksumTracker;
        var sb = new StringBuilder();
        bool all = true;
        uint fakeId = 900_000;

        foreach (var name in names)
        {
            if (!Perturbations.TryGetValue(name, out var perturb))
            {
                sb.AppendLine($"{name}: unknown scenario");
                all = false;
                continue;
            }
            try
            {
                var truth = NetFullCombatState.FromRun(run, null);
                var truthIds = CardIdLayout.Capture(run);
                perturb(run, me);
                var drifted = NetFullCombatState.FromRun(run, null);
                var driftedIds = CardIdLayout.Capture(run);
                if (tracker.GenerateChecksum(truth) == tracker.GenerateChecksum(drifted) && SameIds(run, truthIds, driftedIds))
                {
                    sb.AppendLine($"{name}: SKIP (perturbation changed neither the checksum nor the card IDs)");
                    continue;
                }

                var result = HostStateApplier.Apply(run, truth, drifted, fakeId++, truthIds, driftedIds);
                LastResult = result;
                var after = NetFullCombatState.FromRun(run, null);
                var afterIds = CardIdLayout.Capture(run);
                bool sumOk = tracker.GenerateChecksum(after) == tracker.GenerateChecksum(truth);
                bool idsOk = SameIds(run, truthIds, afterIds);
                // A card outside the combat scope throws on its next draw, discard or play.
                bool scopeOk = me.PlayerCombatState!.AllPiles.Where(x => x.IsCombatPile).SelectMany(x => x.Cards)
                    .All(c => me.Creature.CombatState?.ContainsCard(c) == true);
                bool pass = sumOk && idsOk && scopeOk;
                all &= pass;
                sb.AppendLine($"{name}: {(pass ? "PASS" : "FAIL")} ({result.FixedCount} fix(es)" +
                              $"{(sumOk ? "" : ", checksum differs")}{(idsOk ? "" : ", card IDs differ")}" +
                              $"{(scopeOk ? "" : ", a card is not in the combat scope")})");
                if (!sumOk)
                {
                    sb.AppendLine(Diff(truth.ToString(), after.ToString()));
                }
                if (!idsOk)
                {
                    sb.AppendLine($"    truth ids: {truthIds}");
                    sb.AppendLine($"    after ids: {afterIds}");
                }
                if (!pass)
                {
                    MainFile.Logger.Warn(result.ToLogString());
                }
            }
            catch (Unavailable e)
            {
                // Thrown before the drift changes anything, so nothing is left behind.
                sb.AppendLine($"{name}: SKIP ({e.Message})");
            }
            catch (Exception e)
            {
                all = false;
                sb.AppendLine($"{name}: ERROR {e.GetType().Name}: {e.Message}");
            }
        }

        if (!includeDelta)
        {
            sb.Insert(0, $"hsr selftest: {(all ? "ALL PASSED" : "FAILURES")}\n");
            return (all, sb.ToString().TrimEnd());
        }

        // Delta scenario: the peer drifts, a checkpoint is taken, then a lockstep action (gain 1 energy) runs on the
        // peer before the host snapshot arrives. The resync must keep that +1.
        try
        {
            var combat = me.PlayerCombatState!;
            var truth = NetFullCombatState.FromRun(run, null);
            var ids = CardIdLayout.Capture(run);
            combat.Energy += 3;
            var drifted = NetFullCombatState.FromRun(run, null);
            combat.Energy += 1;
            int expected = truth.Players.First(p => p.playerId == me.NetId).energy + 1;
            HostStateApplier.Apply(run, truth, drifted, fakeId++, ids, ids);
            bool pass = combat.Energy == expected;
            all &= pass;
            sb.AppendLine($"energy-delta: {(pass ? "PASS" : "FAIL")} (energy {combat.Energy}, expected {expected})");
            combat.Energy = expected - 1;
        }
        catch (Exception e)
        {
            all = false;
            sb.AppendLine($"energy-delta: ERROR {e.GetType().Name}: {e.Message}");
        }

        sb.Insert(0, $"hsr selftest: {(all ? "ALL PASSED" : "FAILURES")}\n");
        return (all, sb.ToString().TrimEnd());
    }

    /// <summary>A drift whose precondition is missing (an empty draw pile, no free potion slot); reported as SKIP.</summary>
    private sealed class Unavailable : InvalidOperationException
    {
        public Unavailable(string message) : base(message)
        {
        }
    }

    private static bool SameIds(RunState run, CardIdLayout a, CardIdLayout b) =>
        a.nextId == b.nextId && run.Players.All(p => a.SamePlacement(b, p.NetId));

    private static string Diff(string a, string b)
    {
        var la = a.Split('\n');
        var lb = b.Split('\n');
        var sb = new StringBuilder();
        int n = Math.Max(la.Length, lb.Length);
        for (int i = 0; i < n && sb.Length < 1500; i++)
        {
            string x = i < la.Length ? la[i] : "<missing>";
            string y = i < lb.Length ? lb[i] : "<missing>";
            if (x != y)
            {
                sb.AppendLine($"    truth: {x.Trim()}");
                sb.AppendLine($"    after: {y.Trim()}");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string Status()
    {
        var sb = new StringBuilder();
        var type = RunManager.Instance.NetService?.Type;
        sb.AppendLine($"role: {type}");
        sb.AppendLine($"strikes: {DivergenceStrikes.Describe()}");
        sb.AppendLine(LastResult != null ? LastResult.ToLogString() : "no resync so far");
        return sb.ToString().TrimEnd();
    }
}
#endif
