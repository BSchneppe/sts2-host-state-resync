using System;
using System.Collections.Generic;
using System.Linq;

namespace SlayTheDivergence.Ui;

public enum NoticeKind
{
    /// <summary>Client: this game was repaired from the host's.</summary>
    Resynced,
    /// <summary>Host: a client drifted and is being repaired.</summary>
    Repairing,
    /// <summary>Host: a client keeps drifting and is getting close to the kick.</summary>
    Warning,
    /// <summary>Host: a client was disconnected after too many repairs in a row.</summary>
    Kicked,
    /// <summary>Host: a client produced a matching checksum again.</summary>
    Recovered,
}

public sealed class HistoryEntry
{
    public HistoryEntry(NoticeKind kind, string title, string? subtitle, IReadOnlyList<string> lines, ulong? peer)
    {
        At = DateTime.Now;
        Kind = kind;
        Title = title;
        Subtitle = subtitle;
        Lines = lines;
        Peer = peer;
    }

    public DateTime At { get; }
    public NoticeKind Kind { get; }
    public string Title { get; }
    public string? Subtitle { get; }
    public IReadOnlyList<string> Lines { get; }
    public ulong? Peer { get; }

    /// <summary>Host only: set once the client produced a matching checksum after this divergence.</summary>
    public string? Outcome { get; set; }
}

/// <summary>Every resync notice of the current run, newest last. A new run (new ChecksumTracker) starts empty.</summary>
public static class ResyncHistory
{
    private const int Keep = 100;
    private static readonly List<HistoryEntry> Entries = new();

    public static IReadOnlyList<HistoryEntry> All => Entries;

    public static event Action? Changed;

    /// <summary>Tells the UI; a UI failure must never reach the divergence handling that recorded the entry.</summary>
    private static void Notify()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Resync history display failed: {e.Message}");
        }
    }

    public static void Add(HistoryEntry entry)
    {
        Entries.Add(entry);
        if (Entries.Count > Keep) Entries.RemoveAt(0);
        Notify();
    }

    /// <summary>Marks the peer's open divergences as resolved.</summary>
    public static void Resolve(ulong peer, string outcome)
    {
        foreach (var e in Entries.Where(e => e.Peer == peer && e.Outcome == null && e.Kind != NoticeKind.Recovered))
        {
            e.Outcome = outcome;
        }
        Notify();
    }

    public static void Clear()
    {
        Entries.Clear();
        Notify();
    }

    public static string Describe()
    {
        if (Entries.Count == 0) return "no resyncs this run";
        return string.Join("\n", Entries.Select(e =>
            $"{e.At:HH:mm:ss} {e.Title}{(e.Outcome != null ? $" ({e.Outcome})" : "")}" +
            (e.Subtitle != null ? $"\n    {e.Subtitle}" : "") +
            string.Concat(e.Lines.Select(l => $"\n    {l}"))));
    }
}
