using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Multiplayer.Transport;
using MegaCrit.Sts2.Core.Runs;

namespace SlayTheDivergence.Resync;

/// <summary>
/// Which combat card ID sits at which position of every combat pile, plus the next ID to be handed out.
///
/// The network refers to a card by its combat ID (<see cref="NetCombatCardDb"/>), not by what it is: a played card
/// travels as "card 23", and the host cancels the play without a word if its card 23 is not in hand. The checksum and
/// <c>NetFullCombatState</c> only see card values, so two unupgraded Strikes look identical to them. Rebuilding piles
/// from the host's snapshot by value can therefore produce piles that hash the same while the IDs sit in different
/// places, and the next play of one of those cards desyncs for real. This layout is what the snapshot leaves out.
/// </summary>
public struct CardIdLayout : IPacketSerializable
{
    public const uint NoId = uint.MaxValue;

    public uint nextId;
    public List<PlayerCardIds> players;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(nextId);
        writer.WriteList(players ?? new List<PlayerCardIds>());
    }

    public void Deserialize(PacketReader reader)
    {
        nextId = reader.ReadUInt();
        players = reader.ReadList<PlayerCardIds>();
    }

    public List<uint>? IdsOf(ulong playerId, PileType pileType)
    {
        foreach (var p in players ?? new List<PlayerCardIds>())
        {
            if (p.playerId != playerId) continue;
            foreach (var pile in p.piles ?? new List<PileCardIds>())
            {
                if (pile.pileType == pileType) return pile.ids;
            }
        }
        return null;
    }

    /// <summary>The same IDs in the same piles in the same order (the next ID is compared separately).</summary>
    public bool SamePlacement(CardIdLayout other, ulong playerId)
    {
        var mine = (players ?? new List<PlayerCardIds>()).FirstOrDefault(p => p.playerId == playerId).piles ?? new List<PileCardIds>();
        var theirs = (other.players ?? new List<PlayerCardIds>()).FirstOrDefault(p => p.playerId == playerId).piles ?? new List<PileCardIds>();
        if (mine.Count != theirs.Count) return false;
        foreach (var pile in mine)
        {
            var match = theirs.FirstOrDefault(t => t.pileType == pile.pileType);
            if (match.ids == null || !match.ids.SequenceEqual(pile.ids)) return false;
        }
        return true;
    }

    public static CardIdLayout Capture(RunState run)
    {
        var db = NetCombatCardDb.Instance;
        var layout = new CardIdLayout { nextId = CardIdDb.NextId ?? 0, players = new List<PlayerCardIds>() };
        foreach (var player in run.Players)
        {
            var combat = player.PlayerCombatState;
            if (combat == null) continue;
            var piles = new List<PileCardIds>();
            foreach (var pile in combat.AllPiles.Where(p => p.IsCombatPile))
            {
                piles.Add(new PileCardIds
                {
                    pileType = pile.Type,
                    ids = pile.Cards.Select(c => db.TryGetCardId(c, out var id) ? id : NoId).ToList(),
                });
            }
            layout.players.Add(new PlayerCardIds { playerId = player.NetId, piles = piles });
        }
        return layout;
    }

    public override string ToString() =>
        $"next {nextId}; " + string.Join("; ", (players ?? new List<PlayerCardIds>()).Select(p =>
            $"{p.playerId}: " + string.Join(" ", (p.piles ?? new List<PileCardIds>()).Select(pile => $"{pile.pileType}[{string.Join(",", pile.ids)}]"))));
}

public struct PlayerCardIds : IPacketSerializable
{
    public ulong playerId;
    public List<PileCardIds> piles;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteULong(playerId);
        writer.WriteList(piles ?? new List<PileCardIds>());
    }

    public void Deserialize(PacketReader reader)
    {
        playerId = reader.ReadULong();
        piles = reader.ReadList<PileCardIds>();
    }
}

public struct PileCardIds : IPacketSerializable
{
    public PileType pileType;
    public List<uint> ids;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteInt((int)pileType);
        writer.WriteInt(ids?.Count ?? 0);
        foreach (var id in ids ?? new List<uint>()) writer.WriteUInt(id);
    }

    public void Deserialize(PacketReader reader)
    {
        pileType = (PileType)reader.ReadInt();
        int count = reader.ReadInt();
        // No pre-allocation from a count read off the wire (vanilla ReadList does not either): a corrupt or
        // hostile packet would ask for gigabytes. A count past the packet end fails in ReadUInt instead.
        ids = new List<uint>();
        for (int i = 0; i < count; i++) ids.Add(reader.ReadUInt());
    }
}

/// <summary>
/// Host to client, sent right before the vanilla <c>StateDivergenceMessage</c> for the same checksum: the host's
/// <see cref="CardIdLayout"/> at that checksum. Both go out on the reliable channel, so this one arrives first.
/// The game registers mod message types itself (<c>MessageTypes.Initialize</c>), so every peer needs this build.
/// </summary>
public struct DivergenceCardIdLayoutMessage : INetMessage
{
    public uint checksumId;
    public CardIdLayout layout;

    public bool ShouldBroadcast => false;
    public NetTransferMode Mode => NetTransferMode.Reliable;
    public LogLevel LogLevel => LogLevel.Info;
    public bool ShouldBuffer => true;

    public void Serialize(PacketWriter writer)
    {
        writer.WriteUInt(checksumId);
        writer.Write(layout);
    }

    public void Deserialize(PacketReader reader)
    {
        checksumId = reader.ReadUInt();
        layout = reader.Read<CardIdLayout>();
    }
}

/// <summary>
/// Layouts by checksum ID: our own, recorded at every checkpoint (the host sends these; the client uses its own as the
/// base of the three-way merge), and the host's, as received by the client.
/// </summary>
public static class CardIdLayouts
{
    // ChecksumTracker keeps its last 20 snapshots; a little more slack costs nothing.
    private const int Keep = 32;

    private static readonly Dictionary<uint, CardIdLayout> Local = new();
    private static readonly Dictionary<uint, CardIdLayout> FromHost = new();

    public static void Clear()
    {
        Local.Clear();
        FromHost.Clear();
    }

    public static void RecordLocal(uint checksumId, RunState run) => Put(Local, checksumId, CardIdLayout.Capture(run));

    public static void ReceiveFromHost(uint checksumId, CardIdLayout layout) => Put(FromHost, checksumId, layout);

    public static CardIdLayout? GetLocal(uint checksumId) => Local.TryGetValue(checksumId, out var l) ? l : null;

    public static CardIdLayout? TakeFromHost(uint checksumId) => FromHost.Remove(checksumId, out var l) ? l : null;

    private static void Put(Dictionary<uint, CardIdLayout> map, uint id, CardIdLayout layout)
    {
        map[id] = layout;
        if (map.Count > Keep)
        {
            foreach (var old in map.Keys.Where(k => k + Keep < id).ToList()) map.Remove(old);
        }
    }
}

/// <summary>
/// Reflection access to <see cref="NetCombatCardDb"/>'s private maps, for putting IDs back where the host has them.
/// </summary>
public static class CardIdDb
{
    private static readonly FieldInfo? NextIdField = AccessTools.Field(typeof(NetCombatCardDb), "_nextId");
    private static readonly FieldInfo? IdToCardField = AccessTools.Field(typeof(NetCombatCardDb), "_idToCard");
    private static readonly FieldInfo? CardToIdField = AccessTools.Field(typeof(NetCombatCardDb), "_cardToId");

    public static bool Available => NextIdField != null && IdToCardField != null && CardToIdField != null;

    public static uint? NextId => NextIdField?.GetValue(NetCombatCardDb.Instance) as uint?;

    /// <summary>
    /// Plans an ID rewrite without touching the database. <paramref name="forget"/> drops cards the rebuild takes out of
    /// combat; IDs at or above <paramref name="shiftFrom"/> move by <paramref name="shift"/> (handed out locally since the
    /// checkpoint, they belong to cards the host will number from its own counter); <paramref name="assign"/> gives cards
    /// the host's IDs, and <paramref name="reserved"/> keeps IDs free for cards the rebuild is about to create.
    /// </summary>
    /// <remarks>
    /// A wanted ID can already be held by another card. If that card has left every pile (the database never forgets a
    /// card, so a played Power keeps its ID), it is evicted. If it is in <paramref name="live"/>, still in play and not
    /// being moved, the plan fails: taking its ID would break that card instead. Returns the reason, or null.
    /// </remarks>
    public static string? Plan(IReadOnlyList<(CardModel card, uint id)> assign, IReadOnlyCollection<uint> reserved,
        IReadOnlyCollection<CardModel> forget, IReadOnlySet<CardModel> live, uint shiftFrom, long shift, uint nextId,
        out CardIdRewrite? plan)
    {
        plan = null;
        if (!Available) return "NetCombatCardDb internals changed";

        var cardToId = (Dictionary<CardModel, uint>)CardToIdField!.GetValue(NetCombatCardDb.Instance)!;
        var next = new Dictionary<CardModel, uint>(cardToId);
        foreach (var card in forget) next.Remove(card);

        var assigned = assign.Select(a => a.card).ToHashSet();
        if (shift != 0)
        {
            foreach (var card in next.Keys.ToList())
            {
                if (!assigned.Contains(card) && next[card] >= shiftFrom)
                {
                    next[card] = (uint)(next[card] + shift);
                }
            }
        }

        var wanted = assign.Select(a => a.id).Concat(reserved).ToHashSet();
        int evicted = 0;
        foreach (var (card, id) in next.ToList())
        {
            if (assigned.Contains(card) || !wanted.Contains(id)) continue;
            if (live.Contains(card)) return $"card ID {id} is held by {card}, which is still in play";
            next.Remove(card);
            evicted++;
        }
        foreach (var (card, id) in assign) next[card] = id;

        var byId = new Dictionary<uint, CardModel>();
        foreach (var (card, id) in next)
        {
            if (byId.TryGetValue(id, out var other)) return $"ID {id} would belong to both {other} and {card}";
            byId[id] = card;
        }
        foreach (var id in reserved)
        {
            if (byId.TryGetValue(id, out var holder)) return $"ID {id} is needed for a created card but held by {holder}";
        }
        uint floor = byId.Keys.Concat(reserved).Select(id => id + 1).DefaultIfEmpty(0u).Max();
        plan = new CardIdRewrite(next, Math.Max(nextId, floor), evicted);
        return null;
    }

    /// <summary>
    /// Writes a planned rewrite, plus the IDs of the cards the rebuild created after planning: <paramref name="created"/>
    /// get the host's IDs, <paramref name="unkeyed"/> (no host ID for their slot) keep one they already have or get the
    /// next free one, since a card without an ID throws as soon as it is played. Returns how many cards now hold a
    /// different ID than before.
    /// </summary>
    public static int Commit(CardIdRewrite plan, IReadOnlyList<(CardModel card, uint id)> created, IReadOnlyList<CardModel> unkeyed)
    {
        var db = NetCombatCardDb.Instance;
        var cardToId = (Dictionary<CardModel, uint>)CardToIdField!.GetValue(db)!;
        var idToCard = (Dictionary<uint, CardModel>)IdToCardField!.GetValue(db)!;

        var next = new Dictionary<CardModel, uint>(plan.Next);
        foreach (var (card, id) in created) next[card] = id;
        var used = next.Values.ToHashSet();
        uint nextId = used.Select(id => id + 1).Append(plan.NextId).Max();
        foreach (var card in unkeyed)
        {
            uint id = cardToId.TryGetValue(card, out var given) && !used.Contains(given) ? given : nextId;
            next[card] = id;
            used.Add(id);
            nextId = Math.Max(nextId, id + 1);
        }
        int changed = next.Count(kv => !cardToId.TryGetValue(kv.Key, out var old) || old != kv.Value);

        cardToId.Clear();
        idToCard.Clear();
        foreach (var (card, id) in next)
        {
            cardToId[card] = id;
            idToCard[id] = card;
        }
        NextIdField!.SetValue(db, nextId);
        return changed;
    }
}

/// <summary>A validated card-ID rewrite, ready for <see cref="CardIdDb.Commit"/>.</summary>
public sealed class CardIdRewrite
{
    internal CardIdRewrite(Dictionary<CardModel, uint> next, uint nextId, int evicted)
    {
        Next = next;
        NextId = nextId;
        Evicted = evicted;
    }

    internal Dictionary<CardModel, uint> Next { get; }
    internal uint NextId { get; }

    /// <summary>Cards that had left every pile and gave up an ID the host needs elsewhere.</summary>
    public int Evicted { get; }
}
