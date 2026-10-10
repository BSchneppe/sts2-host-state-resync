using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace SlayTheDivergence.Ui;

/// <summary>
/// Turns the applier's log labels ("player 1001 energy: 5 -> 2") into lines a player can read ("Energy 5 → 2").
/// The labels are written for the log; this only rephrases the ones a player can see in the game.
/// </summary>
public static class ResyncWording
{
    // Longest first, so "max hp" wins over "hp".
    private static readonly (string Key, string Label)[] Fields =
    {
        ("relic grab bag", "Relic pool"),
        ("max potions", "Potion slots"),
        ("card piles", "Cards"),
        ("hand view", ""),
        ("power set", "Powers"),
        ("max hp", "Max HP"),
        ("energy", "Energy"),
        ("potions", "Potions"),
        ("powers", "Powers"),
        ("relics", "Relics"),
        ("stars", "Stars"),
        ("block", "Block"),
        ("gold", "Gold"),
        ("orbs", "Orbs"),
        ("hp", "HP"),
    };

    private static readonly HashSet<string> Scalars = new() { "energy", "stars", "gold", "max potions", "max hp", "hp", "block" };

    private static readonly Regex Player = new(@"^player (\d+) (.+)$");
    private static readonly Regex PlayerCreature = new(@"^creature PlayerId (\d+) (.+)$");
    private static readonly Regex Creature = new(@"^creature (.+)$");
    private static readonly Regex Piles = new(@"(\d+) cards? created, (\d+) removed");
    private static readonly Regex Id = new(@"[+-]?\b(?:[A-Z][A-Z0-9_]*\.)?[A-Z][A-Z0-9_]{2,}\b");

    /// <summary>One readable line per fix a player could notice; internal counters and ID bookkeeping are left out.</summary>
    public static List<string> Lines(IEnumerable<(string What, string Detail)> fixes, ulong localId, Func<ulong, string> name)
    {
        var lines = new List<string>();
        bool rng = false;
        foreach (var (what, detail) in fixes)
        {
            if (what.Contains("rng "))
            {
                if (!rng) lines.Add("Random number streams");
                rng = true;
                continue;
            }
            if (what == "creatures")
            {
                lines.Add("Creatures differ in a way that can't be repaired");
                continue;
            }
            if (what == "other")
            {
                lines.Add(detail);
                continue;
            }
            string owner = "";
            string rest;
            Match m;
            if ((m = Player.Match(what)).Success || (m = PlayerCreature.Match(what)).Success)
            {
                ulong id = ulong.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                owner = id == localId ? "" : $"{name(id)}'s ";
                rest = m.Groups[2].Value;
            }
            else if ((m = Creature.Match(what)).Success)
            {
                rest = m.Groups[1].Value;
                if (rest.EndsWith(" removed", StringComparison.Ordinal))
                {
                    lines.Add($"{rest} ({detail})");
                    continue;
                }
                var field = Fields.FirstOrDefault(f => rest.EndsWith(" " + f.Key, StringComparison.Ordinal));
                if (field.Key == null) continue;
                owner = rest[..^(field.Key.Length + 1)] + "'s ";
                rest = field.Key;
            }
            else
            {
                continue; // card ids, choice ids, reward ids: bookkeeping nobody sees
            }

            var (key, label) = Fields.FirstOrDefault(f => rest == f.Key || rest.StartsWith(f.Key + " ", StringComparison.Ordinal));
            if (key == null || label.Length == 0) continue;
            lines.Add($"{owner}{label}{Describe(key, detail)}".Trim());
        }
        return lines;
    }

    /// <summary>
    /// What the resync could not repair, as at most one line ("Can't repair: Orbs, Nibbit's Strength"). Skips that are
    /// retried at the next checkpoint (changed locally since, left for later) are not listed: they are not stuck.
    /// </summary>
    public static List<string> Unfixed(IEnumerable<(string What, string Why)> skips, ulong localId, Func<ulong, string> name)
    {
        const int MaxSubjects = 3;
        var subjects = new List<string>();
        foreach (var (what, why) in skips)
        {
            if (why.Contains("checkpoint", StringComparison.Ordinal) && !why.Contains("no checkpoint", StringComparison.Ordinal)) continue;
            if (why.Contains("being played", StringComparison.Ordinal)) continue;
            string? subject = what switch
            {
                "creatures" => "Creature list",
                "card ids" or "choice ids" or "reward ids" => null,
                _ when what.Contains("rng ") => "Random number streams",
                _ when what.EndsWith(" turn/phase", StringComparison.Ordinal) => "Turn order",
                _ when what.EndsWith(" hand view", StringComparison.Ordinal) => null,
                _ => Subject(what, localId, name),
            };
            if (subject != null && !subjects.Contains(subject)) subjects.Add(subject);
        }
        if (subjects.Count == 0) return new List<string>();
        string list = string.Join(", ", subjects.Take(MaxSubjects));
        if (subjects.Count > MaxSubjects) list += $" and {subjects.Count - MaxSubjects} more";
        return new List<string> { "Can't repair: " + list };
    }

    /// <summary>"player 1001 orbs" → "Orbs" (or "Bene's Orbs"), "creature Nibbit hp" → "Nibbit's HP", "creature Nibbit" → "Nibbit".</summary>
    private static string? Subject(string what, ulong localId, Func<ulong, string> name)
    {
        Match m;
        string owner = "", rest;
        if ((m = Player.Match(what)).Success || (m = PlayerCreature.Match(what)).Success)
        {
            ulong id = ulong.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            owner = id == localId ? "" : $"{name(id)}'s ";
            rest = m.Groups[2].Value;
        }
        else if ((m = Creature.Match(what)).Success)
        {
            rest = m.Groups[1].Value;
            var field = Fields.FirstOrDefault(f => rest.EndsWith(" " + f.Key, StringComparison.Ordinal));
            if (field.Key == null)
            {
                int power = rest.IndexOf(" power ", StringComparison.Ordinal);
                return power > 0 ? $"{rest[..power]}'s {Pretty(rest[(power + 7)..])}" : rest;
            }
            owner = rest[..^(field.Key.Length + 1)] + "'s ";
            rest = field.Key;
        }
        else
        {
            return null;
        }
        if (rest.StartsWith("power ", StringComparison.Ordinal)) return owner + Pretty(rest[6..]);
        var (key, label) = Fields.FirstOrDefault(f => rest == f.Key || rest.StartsWith(f.Key + " ", StringComparison.Ordinal));
        if (key == null || label.Length == 0) return null;
        return owner + label;
    }

    private static string Describe(string key, string detail)
    {
        if (key == "card piles")
        {
            var m = Piles.Match(detail);
            if (!m.Success) return " back in the host's order";
            int created = int.Parse(m.Groups[1].Value), removed = int.Parse(m.Groups[2].Value);
            var parts = new List<string>();
            if (created > 0) parts.Add($"{created} restored");
            if (removed > 0) parts.Add($"{removed} removed");
            return parts.Count == 0 ? " back in the host's order" : ": " + string.Join(", ", parts);
        }
        if (detail == "reordered") return " back in the host's order";
        if (key == "relic grab bag") return " matched to the host";
        string text = Id.Replace(detail.Replace(" -> ", " → "), match => Pretty(match.Value));
        // A single number ("Energy 2 → 3") reads as one phrase; a list of changes ("Powers: Focus 1 → 2") needs the colon.
        return Scalars.Contains(key) ? " " + text : ": " + text;
    }

    /// <summary>POWER.STRENGTH_POWER → Strength, +VULNERABLE_POWER → +Vulnerable.</summary>
    public static string Pretty(string id)
    {
        string sign = id[0] is '+' or '-' ? id[..1] : "";
        string bare = id.TrimStart('+', '-');
        bare = bare[(bare.LastIndexOf('.') + 1)..]; // POWER.FOCUS_POWER
        foreach (var suffix in new[] { "_POWER", "_POTION", "_RELIC", "_ORB" })
        {
            if (bare.EndsWith(suffix, StringComparison.Ordinal)) bare = bare[..^suffix.Length];
        }
        var words = bare.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant());
        return sign + string.Join(" ", words);
    }
}
