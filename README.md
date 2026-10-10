[![Build](https://github.com/BSchneppe/sts2-host-state-resync/actions/workflows/build.yml/badge.svg)](https://github.com/BSchneppe/sts2-host-state-resync/actions/workflows/build.yml)

# Slay the Divergence (Slay the Spire 2 mod)

Formerly "Host State Resync" (mod ID `HostStateResync`). When upgrading from it, delete the old
`mods/HostStateResync/` folder, or both copies load.

When co-op detects a state divergence, vanilla kicks the client back to the main menu with a
"state divergence" error. This mod keeps the client in the game and repairs its combat state from
the host's instead, and only falls back to the kick when the repair keeps failing. **Every player
in the run has to run the same version** (the manifest marks it `affects_gameplay`, and the game's
join check compares gameplay mods by id and version).

Built against game `v0.111.0` (`41cef1ea`).

## Status

- 2026-09-24: `divergence selftest` (see Testing) passed every scenario in a live singleplayer fight.
- 2026-10-09, two-player Steam sessions:
  - v0.1.0: several drifts at once (energy, gold, run RNG, player RNG, enemy HP, block) healed in
    one resync. The pile-order repair left identical cards holding each other's network IDs, which
    led to a kick (see "Card IDs" below); v0.2.0 fixed that.
  - v0.2.0: block, power, extra card, pile order, swapped identical cards, potion and relic drifts
    healed. The orb drift destroyed an orb that carried another mod's per-orb bonus, which could
    never be recreated, and the client was kicked after 7 strikes.
- v0.3.0 reworks the repair after a code review: stale divergence messages are ignored, counters
  are not merged across turn-start resets, card IDs are validated before any pile is rebuilt,
  collections keep the host's order, and the hand view is synced. It builds and its self-test
  scenarios were updated, but it has **not been run in game yet**.

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
  `host + (now - base)`. Once a player's turn or phase moved on since N, the fields turn setup can
  reset outright (energy, stars, block, power amounts) wait for the next checkpoint instead, unless
  they already equal the host's value: a reset can even land on the old value and look untouched.
- **RNG streams and sync markers** (choice ids, reward ids): left alone if they matched at N;
  otherwise loaded from the host and advanced by the local draws since N (each draw is exactly one
  generator step).
- **Collections** (card piles, potions, relics, power set, relic grab bag, orbs): only replaced if
  unchanged locally since N, and put back in the host's order, which the checksum hashes.
- **Card piles and card IDs** are planned for every player first, then the card-ID rewrite is
  validated, and only then are both applied together (see below).

The repair writes through the game's own mutators with `silent: true` or through private setters
and fields. The actions that would normally cause a change (card plays, power and relic triggers)
do not run, so their hooks and VFX do not fire; a few setters still raise their own change events
(energy, stars, power amounts). Most UI is not told. The hand view is synced explicitly: cards that
left the hand lose their node, cards that came in get one at their place in the pile; cards already
shown keep their order. The orb display is synced slot by slot. Potion, relic and power displays
may show their old order until they next change. Both sides get a short on-screen text and a full
per-field log line tagged `[SlayTheDivergence]`.

### Card IDs

A card play travels over the network as "combat card 23" (`NetCombatCardDb`), and the host silently
cancels the play if its card 23 is not in hand. The checksum only sees card values, so a rebuild
that puts two identical Strikes back in each other's slots hashes clean and still breaks the next
play of either. Every peer therefore records which ID sits in which pile slot at every checkpoint,
and on a mismatch the host sends its layout (`DivergenceCardIdLayoutMessage`) on the reliable channel just
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
| creature power set | yes, gated | `ApplyInternal` / `RemoveInternal`, then reordered to the host's order; a power the host has at amount 0 cannot be added |
| potions | yes, gated | `AddPotionInternal` / `DiscardPotionInternal`, reordered within the occupied slots |
| relics (set and saved-property counters) | yes, gated | `AddRelicInternal` / `RemoveRelicInternal` / `SavedProperties.Fill`, then reordered |
| relic grab bag | yes, gated | `RelicGrabBag.LoadFromSerializable` |
| card piles (order and contents) | yes, gated | matched as described under "Card IDs"; created cards go into the combat scope |
| combat card IDs (`NetCombatCardDb`) | yes | the host's layout, validated before any pile changes |
| next choice ids, next reward ids | yes | `FastForwardChoiceIds` / `FastForwardRewardIds` |
| orb order | yes, gated | `OrbQueue.Remove/Insert` of the existing orbs; the display follows through `NOrb.ReplaceOrb` |
| orb values | partly | only Dark's evoke value and Glass's passive value, plain fields the orbs keep themselves; the rest comes from Focus, relics and other mods' hooks |
| orb set (an orb missing or extra) | yes, gated | orbs of the same type are reused (they keep other mods' per-orb state); a missing one is created from its id, an extra one removed without its evoke; every slot node is then pointed at its orb, so a created orb can be evoked. A created orb starts without other mods' per-orb state, and a value that differs because of it is reported as a skip |
| card affliction / modified energy cost / keywords / enchantment | no | not writable; a mismatch left after the rebuild is reported as a skip |
| creature set: an enemy only we still have | yes, gated | removed the way a death removes it (node, powers, combat lists), without the death hooks, whose results arrive with the host's state; only when we already had more of that monster at N, never allies or pets; the last enemy going triggers the win check |
| creature set: a creature only the host has | no | a monster or summon that spawned on the host only; creating creatures is not repaired |
| turn number / turn phase | no | flow control; the peers would need to be in the same async action anyway |
| last executed action id / hook id | n/a | informational |
| anything outside `NetFullCombatState` | no | not in the checksum, so never detected in the first place |

## Known limitations

- Whatever is not fixable above keeps diverging at every checkpoint, so the host kicks the client
  after 7 strikes: the vanilla outcome, only later.
- State that other mods keep per card or orb instance, outside `NetFullCombatState`, cannot be
  carried over when a card is created.
- An HP repair can set a creature to 0 HP without the game's death handling.
- A removed enemy's death hooks (relics or powers that react to a kill) do not run on the client; their
  effects arrive with the host's state only where `NetFullCombatState` covers them. Removal waits for
  the players' turn, because a monster can still start its move after the last check. Removing the
  last enemy has only been run by the self-test.
- Creatures carry no ID in the snapshot, so identical monsters are told apart by HP. If two copies
  are close in HP and one died on the host, the wrong copy can be removed.
- An orb created by a resync starts without other mods' per-orb state, and is not counted in the
  combat history as channeled. If the host has more orbs than we have slots, orbs are not repaired.
- A power the host has at amount 0 cannot be added (`ApplyInternal` ignores 0), so that difference
  stays.
- The snapshot does not say which potion slots the host's potions sit in, so potions are only
  reordered within the client's occupied slots.
- Replays written after a resync do not reproduce, since the resync is not a recorded action.

## Installation

Every player in the run needs the same version: the game compares gameplay mods by id and version
when someone joins.

1. Download `SlayTheDivergence-vX.Y.Z.zip` from [Releases](../../releases)
2. Unzip it into `Slay the Spire 2/mods/`, so the files end up in `mods/SlayTheDivergence/`
3. Launch the game and enable the mod in the Mods menu

## Building from source

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```bash
dotnet build -c Release   # what Releases and the Workshop ship
dotnet build -c Debug     # adds the divergence test command (see Testing)
```

With Slay the Spire 2 installed, the build references `sts2.dll`, `0Harmony.dll` and `GodotSharp.dll`
from the game and copies `SlayTheDivergence.dll` and `SlayTheDivergence.json` into
`mods/SlayTheDivergence/`. The paths are auto-detected on macOS, Windows and Linux; override them with
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

- **GitHub Release:** push a tag `vX.Y.Z` that matches the version in `SlayTheDivergence.json`. The
  Release workflow builds Release, audits the patch targets and attaches `SlayTheDivergence-vX.Y.Z.zip`.
- **Steam Workshop:** `STEAM_USER=<account> ./publish.sh "change note"` builds Release and uploads it
  with steamcmd. The first run creates a private item and saves its ID to `.workshop_id`.

## Verifying it works

1. Start a co-op run with every player on the same version. The game's log (`logs/godot.log` in
   the game's user data folder) contains `SlayTheDivergence initialized` once the patches are in.
2. Drift one peer on purpose with a Debug build (`divergence desync <kind>`, see Testing).
3. At the next checkpoint the host log shows `Client <id> diverged ... keeping it connected`, the
   client log shows `[SlayTheDivergence] checksum #N: applied ... fix(es)`, and the checkpoint after
   that produces no divergence message.

## Testing

Both layers are driven from the in-game dev console (the key left of 1; debug commands are enabled
whenever the game runs modded). The command is `Testing/DivergenceConsoleCmd.cs`, and its subcommands and
drift kinds tab-complete. **Debug builds only** (`#if DEBUG`): Release builds ship without `divergence`.

**Fake second player, no network: `divergence selftest`.** Singleplayer only, in any combat. For every
drift kind it snapshots the live state as the "host truth", drifts the live state the way a
diverged client would look, snapshots that as the client's own checkpoint, runs
`HostStateApplier.Apply`, and asserts that the live state hashes back to the truth with the game's
own `ChecksumTracker.GenerateChecksum`, that every combat card ID is back in its slot, that every
card in the piles belongs to the combat scope, and that the orb display shows the orbs. The
kinds: energy, gold, run RNG, player RNG, enemy HP, block, power amount, pile order, extra card,
potion, relic, orb order, choice ids, two identical cards swapping places (`swapids`), a
vanished card (`losecard`), a vanished power (`losepower`) and a vanished orb (`loseorb`). Two
scenarios change the host snapshot instead: `dropenemy` (an enemy only we still have; needs two
living enemies) and `dropallenemies` (every enemy; the combat ends, so it only runs when named).
`removeenemy` is co-op only. A kind whose precondition is missing (an empty draw pile, no free
potion slot) reports SKIP. The full run adds `energy-delta`, a lockstep
action after the checkpoint that the merge has to keep. `divergence selftest <kind>` runs one;
failures print a line diff of the two state dumps and of the two ID layouts.

**Real second player: `divergence desync <kind>`.** Drifts only the peer that runs it (the command is not
networked), so the next checkpoint diverges for real. Run it on a client, except `removeenemy`, which
runs on the host only (in a fight with two or more enemies) and leaves the client with a monster the
host no longer has. `loseorb` on a client makes the client recreate an orb; on the host, the client
has to remove one. `divergence status` prints strike counts and the last resync result.
`tools/localmp.sh` starts one host and N clients of the installed game over ENet on
`127.0.0.1:33771` without Steam, using the game's `--fastmp=host_standard` /
`--fastmp=join --clientId=1001` developer shortcut. It looks for the macOS install by default; set
`STS2_APP` elsewhere.

## License

[MIT](LICENSE)
