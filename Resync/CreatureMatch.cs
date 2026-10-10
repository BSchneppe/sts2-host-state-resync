using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace SlayTheDivergence.Resync;

/// <summary>
/// Pairs up two creature lists that may differ by creatures one side has removed (a monster that died on one peer
/// only). Creatures pair with creatures of the same identity (monster id or player id); among identical monsters the
/// pairing keeps their order and picks the one closest in HP, so the copy left over is the one that drifted.
/// </summary>
public static class CreatureMatch
{
    public readonly record struct Key(ModelId? Monster, ulong? Player, int Hp);

    /// <summary>
    /// For every index of <paramref name="local"/>, the index of its partner in <paramref name="target"/>, or -1 for a
    /// creature the target does not have. Null when the target has a creature <paramref name="local"/> lacks, unless
    /// <paramref name="partial"/>: then the local creatures are paired with their closest targets and the rest of the
    /// targets are left over (used for our own checkpoint snapshot, where a monster may have died here since).
    /// </summary>
    public static int[]? Align(IReadOnlyList<Key> target, IReadOnlyList<Key> local, bool partial = false)
    {
        var map = Enumerable.Repeat(-1, local.Count).ToArray();
        foreach (var group in Enumerable.Range(0, target.Count).GroupBy(i => (target[i].Monster, target[i].Player)))
        {
            var t = group.ToList();
            var l = Enumerable.Range(0, local.Count).Where(i => (local[i].Monster, local[i].Player) == group.Key).ToList();
            if (l.Count < t.Count)
            {
                if (!partial) return null;
                var chosen = ClosestSubsequence(t.Select(i => target[i].Hp).ToList(), l.Select(i => local[i].Hp).ToList());
                for (int j = 0; j < l.Count; j++) map[l[j]] = t[chosen[j]];
                continue;
            }
            var picked = ClosestSubsequence(l.Select(i => local[i].Hp).ToList(), t.Select(i => target[i].Hp).ToList());
            for (int j = 0; j < t.Count; j++) map[l[picked[j]]] = t[j];
        }
        return map;
    }

    /// <summary>Indices into <paramref name="have"/>, in order, one per entry of <paramref name="want"/>, minimising the HP gap.</summary>
    private static int[] ClosestSubsequence(List<int> have, List<int> want)
    {
        int m = have.Count, k = want.Count;
        var cost = new long[m + 1, k + 1];
        for (int j = 1; j <= k; j++) cost[0, j] = long.MaxValue / 4;
        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= k; j++)
            {
                long skip = cost[i - 1, j];
                long take = cost[i - 1, j - 1] + Math.Abs(have[i - 1] - want[j - 1]);
                cost[i, j] = Math.Min(skip, take);
            }
        }
        var picked = new int[k];
        for (int i = m, j = k; j > 0; i--)
        {
            if (cost[i, j] != cost[i - 1, j])
            {
                picked[--j] = i - 1;
            }
        }
        return picked;
    }
}
