[![Build](https://github.com/BSchneppe/sts2-host-state-resync/actions/workflows/build.yml/badge.svg)](https://github.com/BSchneppe/sts2-host-state-resync/actions/workflows/build.yml)

# Host State Resync (Slay the Spire 2 mod)

When co-op detects a state divergence, vanilla kicks the client back to the main menu with a
"state divergence" error. This mod keeps the client in the game and repairs its combat state from
the host's instead, and only falls back to the kick when the repair keeps failing. **Every player
in the run has to run the same version** (the manifest marks it `affects_gameplay`, and the game's
join check compares gameplay mods by id and version).

Built against game `v0.111.0` (`41cef1ea`).

## Status

- 2026-09-24: `hsr selftest` (see Testing) passed every scenario in a live singleplayer fight.
- 2026-10-09, two-player Steam sessions:
  - v0.1.0: several drifts at once (energy, gold, run RNG, player RNG, enemy HP, block) healed in
    one resync. The pile-order repair left identical cards holding each other's network IDs, which
    led to a kick (see "Card IDs" below); v0.2.0 fixed that.
  - v0.2.0: block, power, extra card, pile order, swapped identical cards, potion and relic drifts
    healed. The orb drift destroyed an orb that carried another mod's per-orb bonus, which could
    never be recreated, and the client was kicked after 7 strikes.
- v0.3.0 reworks the repair after a code review: stale divergence messages are ignored, counters
  are not merged across turn-start resets, card IDs are validated before any pile is rebuilt, orbs
  are only reordered, collections keep the host's order, and the hand view is rebuilt. It builds and
  its self-test scenarios were updated, but it has **not been run in game yet**.

## How vanilla detects and handles a desync

`ChecksumTracker` (in `MegaCrit.Sts2.Core.Multiplayer.Game`) hashes a `NetFullCombatState`
snapshot at eight checkpoints: after every action, after player/enemy turn start, after enemy turn
end, after both player end-turn phases, and when leaving an event or rest site. Clients send the
hash to the host; the host compares.

1. Host `CompareChecksums`: mismatch → sends the client a `StateDivergenceMessage` that contains
   the host's **full `NetFullCombatState` snapshot** for that checksum id.
2. Client `OnReceivedStateDivergenceMessage` → `LogStateDivergence`: logs both dumps, reports to
   Sentry (which the game turns off when it runs modded), echoes its own snapshot back to the host,
   fires `StateDiverged` (client side that only writes a replay).
3. Host `OnReceivedStateDivergenceMessage`: logs both dumps, fires `StateDiverged` (host side that
   opens the error popup) and calls `DisconnectClient(sender, NetError.StateDivergence)`. That
   disconnect is what throws the client to the menu.

There is no repair path in vanilla. The rejoin flow that ships full state
(`ClientRejoinResponseMessage` with `SerializableRun` + `NetFullCombatState`) is only half done:
the client side throws `NotImplementedException("Run re-joining has yet to be implemented!")`. So
a fix has to patch state in place.

## What RitsuLib hooks

RitsuLib (as of 0.6.2) only adds diagnostics; it never changes the outcome:

| RitsuLib patch | Game target | What it does |
|---|---|---|
| `RitsuLibSidecarChecksumDivergenceRelayPatch` | prefix `ChecksumTracker.CompareChecksums` | host only, triggers its sidecar diagnostic dump on mismatch |
| `StateDivergenceDiagnosticsLogPatch` | prefix `ChecksumTracker.LogStateDivergence` | builds the field-by-field diff report and the `ritsulib_state_divergence_*.zip` bundle |
| `StateDivergenceDiagnosticsPopupCreatePatch` | postfix `NErrorPopup.Create(NetErrorInfo)` | attaches the report to the reason-105 popup |
| `StateDivergenceDiagnosticsPopupReadyPatch` | postfix `NErrorPopup._Ready` | adds the "details" button |

This mod still invokes the original `LogStateDivergence`, so RitsuLib's report and bundle keep
working alongside it.

## What this mod does

Harmony patches, in `Patches/`:

- `ChecksumTracker.OnReceivedStateDivergenceMessage` is replaced.
  - Host: counts a strike for the client and still logs, but does **not** disconnect unless the
    client diverged more than `Settings.MaxConsecutiveStrikes` (6) times without a matching
    checksum in between.
  - Client: ignores messages for checkpoints taken before its last resync (their snapshots still
    carry the drift that resync repaired), skips messages whose own snapshot has fallen out of the
    tracker's 20-entry history, and otherwise runs the vanilla logging and then
    `HostStateApplier.Apply` with the host's snapshot.
- `ChecksumTracker.CompareChecksums`: a prefix sends the client the host's card-ID layout for a
  mismatching checkpoint, ahead of the vanilla `StateDivergenceMessage`; a postfix resets the
  client's strikes on a matching checksum.
- `RunManager.StateDiverged` is skipped (no modal popup on the host, no "abandoning run" on the
  client), except when the host has decided to kick after all. The client still writes the replay.
- The `ChecksumTracker` constructor records every peer's card-ID layout at every checkpoint,
  registers the handler for the host's layouts and clears the strike counts for the new run;
  `Dispose` unregisters the handler.

If any patch fails to apply (say, after a game update renamed a target), all of them are removed
again and the game handles divergences as in vanilla.

`Resync/HostStateApplier.cs` does the repair. The host's snapshot is from checkpoint N, but by the
time it arrives the client may have run more lockstep actions, so it is a three-way merge against
the client's own snapshot at N:

- **Counters** (gold, max potions, energy, stars, HP, max HP, block, power amounts): left alone if
  they matched the host at N; set to the host's value if unchanged since N; otherwise
  `host + (now - base)`, except when a player's turn or phase moved on since N, where the change
  may be a turn-start reset (energy refill, block clearing) and the field waits for the next
  checkpoint.
- **RNG streams and sync markers** (choice ids, reward ids): left alone if they matched at N;
  otherwise loaded from the host and advanced by the local draws since N (each draw is exactly one
  generator step).
- **Collections** (card piles, potions, relics, power set, relic grab bag, orbs): only replaced if
  unchanged locally since N, and put back in the host's order, which the checksum hashes.
- **Card piles and card IDs** are planned for every player first, then the card-ID rewrite is
  validated, and only then are both applied together (see below).

The repair writes through the game's own mutators with `silent: true` or through private setters
and fields: no gameplay hooks or VFX fire, and most UI is not told either. The hand view is rebuilt
explicitly; potions, relics, powers and orbs may show their old order until they next change. Both
sides get a short on-screen text and a full per-field log line tagged `[HostStateResync]`.

### Card IDs

A card play travels over the network as "combat card 23" (`NetCombatCardDb`), and the host silently
cancels the play if its card 23 is not in hand. The checksum only sees card values, so a rebuild
that puts two identical Strikes back in each other's slots hashes clean and still breaks the next
play of either. Every peer therefore records which ID sits in which pile slot at every checkpoint,
and on a mismatch the host sends its layout (`HsrCardIdLayoutMessage`) on the reliable channel just
before the vanilla divergence message.

The client matches its cards to the host's slots in passes, strictest first: the same card state
(card, affliction, cost, keywords) under the host's ID, the same state under any ID, the same card
and upgrade under the host's ID, the same card and upgrade. Missing cards are created in the combat
scope. The ID rewrite is planned before anything changes: a host ID still held by a card that has
left every pile (the database never forgets a card) is taken from it, while an ID held by a card
that is still in play fails the plan, and then neither piles nor IDs are touched. IDs handed out
locally since N move with the counter (`host + (now - base)`).

The new message changes the game's network message numbering (mod message types are sorted in
with the game's own), which is one more reason every player needs the same version.

## Which divergences are fixable

`NetFullCombatState` is everything the checksum covers, so it is also the complete list of what
can differ. Desyncs seen in real sessions were a player's energy after the first end-turn phase and
the run's `Niche` RNG stream (once when leaving a modded Ancient event, once at turn start), both
repairable.

| Field | Fixable | How / why not |
|---|---|---|
| run RNG, player RNG (state + counter) | yes | `Rng.LoadFromSerializable`, then replay the local draws; skipped if the stream was reset since N |
| player energy, stars, gold, max potions | yes | public setters, `AddToMaxPotionCount` |
| creature HP / max HP / block | yes | private setters via Harmony `PropertySetter` |
| creature power amounts | yes | `PowerModel.SetAmount`; duplicate (instanced) powers matched one by one |
| creature power set | yes, gated | `ApplyInternal` / `RemoveInternal`, then reordered to the host's order |
| potions | yes, gated | `AddPotionInternal` / `DiscardPotionInternal`, reordered within the occupied slots |
| relics (set and saved-property counters) | yes, gated | `AddRelicInternal` / `RemoveRelicInternal` / `SavedProperties.Fill`, then reordered |
| relic grab bag | yes, gated | `RelicGrabBag.LoadFromSerializable` |
| card piles (order and contents) | yes, gated | matched as described under "Card IDs"; created cards go into the combat scope |
| combat card IDs (`NetCombatCardDb`) | yes | the host's layout, validated before any pile changes |
| next choice ids, next reward ids | yes | `FastForwardChoiceIds` / `FastForwardRewardIds` |
| orb order | yes, gated | `OrbQueue.Remove/Insert` of the existing orbs |
| orb values | partly | only state an orb accumulates itself (Dark's evoke, Glass's passive); the rest comes from Focus, relics and other mods' hooks |
| orb set (an orb missing or extra) | no | a created orb has no UI node (evoking it throws) and loses other mods' per-orb state |
| card affliction / modified energy cost / keywords / enchantment | no | not writable; a mismatch left after the rebuild is reported as a skip |
| creature set (different count or identity) | no | a monster that died or spawned on one side only |
| turn number / turn phase | no | flow control; the peers would need to be in the same async action anyway |
| last executed action id / hook id | n/a | informational |
| anything outside `NetFullCombatState` | no | not in the checksum, so never detected in the first place |

## Known limitations

- Whatever is not fixable above keeps diverging at every checkpoint, so the host kicks the client
  after 7 strikes: the vanilla outcome, only later.
- State that other mods keep per card or orb instance, outside `NetFullCombatState`, cannot be
  carried over when a card is created.
- An HP repair can set a creature to 0 HP without the game's death handling.
- The snapshot does not say which potion slots the host's potions sit in, so potions are only
  reordered within the client's occupied slots.
- Replays written after a resync do not reproduce, since the resync is not a recorded action.

## Installation

Every player in the run needs the same version: the game compares gameplay mods by id and version
when someone joins.

1. Download `HostStateResync-vX.Y.Z.zip` from [Releases](../../releases)
2. Unzip it into `Slay the Spire 2/mods/`, so the files end up in `mods/HostStateResync/`
3. Launch the game and enable the mod in the Mods menu

## Building from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
dotnet build -c Release   # what Releases and the Workshop ship
dotnet build -c Debug     # adds the hsr test command (see Testing)
```

With Slay the Spire 2 installed, the build references `sts2.dll`, `0Harmony.dll` and `GodotSharp.dll`
from the game and copies `HostStateResync.dll` and `HostStateResync.json` into
`mods/HostStateResync/`. The paths are auto-detected on macOS, Windows and Linux; override them with
`-p:STS2GameDir=/path/to/game/data -p:STS2ModsDir=/path/to/mods`. Without a game install (CI), the
build uses the `BSchneppe.Sts2.ReferenceAssemblies` NuGet package for the version in
`RefAssembliesVersion`.

`tools/PatchAudit` checks that every Harmony patch target still exists. CI runs it against the
reference assemblies, which only carry public members, so the private targets are exempted there;
against an installed game it checks them all:

```bash
dotnet run --project tools/PatchAudit -- . "<game data dir>/sts2.dll"
```

## Publishing

- **GitHub Release:** push a tag `vX.Y.Z` that matches the version in `HostStateResync.json`. The
  Release workflow builds Release, audits the patch targets and attaches `HostStateResync-vX.Y.Z.zip`.
- **Steam Workshop:** `STEAM_USER=<account> ./publish.sh "change note"` builds Release and uploads it
  with steamcmd. The first run creates a private item and saves its ID to `.workshop_id`.

## Verifying it works

1. Start a co-op run with every player on the same version. The game's log (`logs/godot.log` in
   the game's user data folder) contains `HostStateResync initialized` once the patches are in.
2. Drift one peer on purpose with a Debug build (`hsr desync <kind>`, see Testing).
3. At the next checkpoint the host log shows `Client <id> diverged ... keeping it connected`, the
   client log shows `[HostStateResync] checksum #N: applied ... fix(es)`, and the checkpoint after
   that produces no divergence message.

## Testing

Both layers are driven from the in-game dev console (the key left of 1; debug commands are enabled
whenever the game runs modded). The command is `Testing/HsrConsoleCmd.cs`, and its subcommands and
drift kinds tab-complete. **Debug builds only** (`#if DEBUG`): Release builds ship without `hsr`.

**Fake second player, no network: `hsr selftest`.** Singleplayer only, in any combat. For every
drift kind it snapshots the live state as the "host truth", drifts the live state the way a
diverged client would look, snapshots that as the client's own checkpoint, runs
`HostStateApplier.Apply`, and asserts that the live state hashes back to the truth with the game's
own `ChecksumTracker.GenerateChecksum`, that every combat card ID is back in its slot, and that
every card in the piles belongs to the combat scope. The kinds: energy, gold, run RNG, player RNG,
enemy HP, block, power amount, pile order, extra card, potion, relic, orb order, choice ids, two
identical cards swapping places (`swapids`), a vanished card (`losecard`) and a vanished power
(`losepower`). A kind whose precondition is missing (an empty draw pile, no free potion slot)
reports SKIP. The full run adds `energy-delta`, a lockstep action after the checkpoint that the
merge has to keep. `hsr selftest <kind>` runs one; failures print a line diff of the two state
dumps and of the two ID layouts.

**Real second player: `hsr desync <kind>`.** Drifts only the peer that runs it (the command is not
networked), so the next checkpoint diverges for real; `hsr status` prints strike counts and the
last resync result. `tools/localmp.sh` starts one host and N clients of the installed game over
ENet on `127.0.0.1:33771` without Steam, using the game's `--fastmp=host_standard` /
`--fastmp=join --clientId=1001` developer shortcut. It looks for the macOS install by default; set
`STS2_APP` elsewhere.

## License

[MIT](LICENSE)
