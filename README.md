# Host State Resync (Slay the Spire 2 mod)

When co-op detects a state divergence, vanilla kicks the client back to the main menu with a
"state divergence" error. This mod keeps the client in the game and overwrites its combat state
with the host's instead. **Every peer has to run it** (the manifest marks it `affects_gameplay`,
so the game's handshake enforces that).

Built against game `v0.111.0` (`41cef1ea`). `hsr selftest` (synthetic host snapshot, see Testing)
passes all 12 scenarios in a live singleplayer fight as of 2026-09-24. A real two-client desync has
not been exercised yet.

## How vanilla detects and handles a desync

`ChecksumTracker` (in `MegaCrit.Sts2.Core.Multiplayer.Game`) hashes a `NetFullCombatState`
snapshot at eight checkpoints: after every action, after player/enemy turn start, after enemy turn
end, after both player end-turn phases, and when leaving an event or rest site. Clients send the
hash to the host; the host compares.

1. Host `CompareChecksums`: mismatch → sends the client a `StateDivergenceMessage` that contains
   the host's **full `NetFullCombatState` snapshot** for that checksum id.
2. Client `OnReceivedStateDivergenceMessage` → `LogStateDivergence`: logs both dumps, reports to
   Sentry, echoes its own snapshot back to the host, fires `StateDiverged` (client side that only
   writes a replay).
3. Host `OnReceivedStateDivergenceMessage`: logs both dumps, fires `StateDiverged` (host side that
   opens the error popup) and calls `DisconnectClient(sender, NetError.StateDivergence)`. That
   disconnect is what throws the client to the menu.

There is no repair path in vanilla. The rejoin flow that ships full state
(`ClientRejoinResponseMessage` with `SerializableRun` + `NetFullCombatState`) is only half done:
the client side literally throws `NotImplementedException("Run re-joining has yet to be
implemented!")`, and the other join screen shows a "run in progress" error. So a fix has to patch
state in place.

## What RitsuLib hooks

RitsuLib 0.6.2 is diagnostics only, it never changes the outcome:

| RitsuLib patch | Game target | What it does |
|---|---|---|
| `RitsuLibSidecarChecksumDivergenceRelayPatch` | prefix `ChecksumTracker.CompareChecksums` | host only, triggers its sidecar diagnostic dump on mismatch |
| `StateDivergenceDiagnosticsLogPatch` | prefix `ChecksumTracker.LogStateDivergence` | builds the field-by-field diff report and the `ritsulib_state_divergence_*.zip` bundle |
| `StateDivergenceDiagnosticsPopupCreatePatch` | postfix `NErrorPopup.Create(NetErrorInfo)` | attaches the report to the reason-105 popup |
| `StateDivergenceDiagnosticsPopupReadyPatch` | postfix `NErrorPopup._Ready` | adds the "details" button |

This mod still invokes the original `LogStateDivergence`, so RitsuLib's report and bundle keep
working alongside it.

## What this mod does

Three Harmony patches in `Patches/StateDivergencePatches.cs`:

- `ChecksumTracker.OnReceivedStateDivergenceMessage` is replaced. Host: counts a strike for the
  client, still logs, but does **not** disconnect unless the client has diverged more than
  `Settings.MaxConsecutiveStrikes` (6) times without a matching checksum in between. Client: after
  the vanilla logging, calls `HostStateApplier.Apply` with the host's snapshot.
- `ChecksumTracker.CompareChecksums` postfix: a matching checksum resets that client's strikes.
- `RunManager.StateDiverged` is skipped (no modal popup on host, no "abandoning run" on client),
  except when the host has decided to kick after all. The client still writes the divergence replay.

`Resync/HostStateApplier.cs` does the actual override. The host snapshot is from checksum N, but
by the time it arrives the client may have executed more lockstep actions, so it is a **three-way
merge** against the client's own snapshot at N (the `ChecksumTracker` keeps the last 20):

- counters (HP, max HP, block, energy, stars, gold, power amounts, choice ids, reward ids):
  `target = host + (now - base)`;
- RNG streams (run RNG set and per-player RNG set): load the host's state, then advance by the
  number of local draws since N;
- collections (card piles, potions, relic set and relic saved properties, relic grab bag, power
  set): replaced with the host's version only if unchanged locally since N, otherwise left for the
  next checkpoint, which delivers a fresher snapshot if they still differ.

Everything goes through the game's own `*Internal` mutators (`SetAmount`, `ApplyInternal`,
`AddInternal`, `RemoveInternal`, `AddPotionInternal`, `LoadFromSerializable`, `FastForward*`) so UI
events fire but no gameplay hooks or VFX do. Both sides get a short on-screen text and a full
per-field log line tagged `[HostStateResync]`.

## Which divergences are fixable

`NetFullCombatState` is everything the checksum covers, so it is also the complete list of what
can differ. The three real desyncs in the bug report from 2026-09-23 were:

| Log | Context | Only difference | Fixable |
|---|---|---|---|
| `godot2026-09-22T20.31.24.log` | After player turn phase one end | player energy 3 vs 0 | yes, scalar |
| `godot.log` #0 | Exiting event `HADESANCIENTS-HECATE_ANCIENT` | `Niche` RNG counter 811 vs 0 | yes, RNG state |
| `godot.log` #21 | After player turn start | `Niche` RNG counter 62 vs 2 | yes, RNG state |

Per field:

| Field | Fixable | How / why not |
|---|---|---|
| run RNG, player RNG (state + counter) | yes | `Rng.LoadFromSerializable` then replay local draws |
| creature HP / max HP / block | yes | private setters via Harmony `PropertySetter`, events fire |
| creature power amounts | yes | `PowerModel.SetAmount` |
| creature power set (missing/extra power) | yes, gated | `ToMutable().ApplyInternal` / `RemoveInternal`; only if unchanged since checkpoint |
| player energy, stars, gold, max potions | yes | public setters, `AddToMaxPotionCount` |
| potions | yes, gated | `AddPotionInternal` / `DiscardPotionInternal` |
| relics (set and saved-property counters) | yes, gated | `AddRelicInternal` / `RemoveRelicInternal` / `SavedProperties.Fill` |
| relic grab bag | yes, gated | `RelicGrabBag.LoadFromSerializable` |
| card piles (order and contents) | yes, gated | pool all combat cards, redistribute in host order (the card holding the host's combat ID for that slot first, else by value), create via `CardModel.FromSerializable`, drop leftovers |
| combat card IDs (`NetCombatCardDb`) | yes | host sends its ID layout (`HsrCardIdLayoutMessage`) before the divergence message; cards are re-keyed to the host's IDs and the ID counter merged as `host + (now - base)` |
| next choice ids, next reward ids | yes | `FastForwardChoiceIds` / `FastForwardRewardIds` |
| creature set (different count or identity) | no | a monster that died or spawned on one side only; creating or deleting creatures mid-combat is not something this mod attempts |
| turn number / turn phase | no | flow control; the peers would need to be in the same async action anyway |
| orb queue | yes, gated | identity and order rebuilt via `OrbQueue.Remove/Insert`; Dark orb evoke value set via its private field; passive/evoke values otherwise derive from powers |
| card affliction / modified energy cost / keywords | no | not carried in a way that can be written back |
| last executed action id / hook id | n/a | informational |
| anything outside `NetFullCombatState` | no | not in the checksum, so never detected in the first place |

Card piles need the card IDs, not just the cards. A card play travels as "combat card 23", and the
host silently cancels it if its card 23 is not in hand. The checksum only sees card values, so a
rebuild that puts two identical Strikes back in each other's slots hashes clean and still breaks the
next play of either. That happened in a real session (v0.1.0, 2026-10-09): after `hsr desync shuffle`
was "healed", a Strike was cancelled on the host only, a monster died on the client only, and the
creature-set mismatch got the client kicked. Since v0.2.0 every peer records its ID layout at every
checkpoint, the host sends its layout ahead of the vanilla divergence message, and the client places
and re-keys cards by ID. The new message changes the game's message numbering, which is why the
version was bumped: the join check compares gameplay mods by `id-version`, so mixed builds refuse to
connect instead of misreading each other's messages.

## Install / build

```bash
dotnet build -c Debug
```

On this machine `DOTNET_ROOT=$HOME/.dotnet` is needed. The build copies `HostStateResync.dll` and
`HostStateResync.json` into the game's `mods/HostStateResync/` folder (path overridable with
`-p:STS2ModsDir=...`). Give the same folder to every other player.

## Verifying it works

1. Start a co-op run with all peers running the mod; `modloader.log` should list `HostStateResync`.
2. Force a desync on the client, for example with a client-only gameplay mod that consumes the
   `Niche` RNG, or by hand-editing a value.
3. On the next checkpoint the host log shows `Client <id> diverged ... keeping it connected`, the
   client log shows `[HostStateResync] checksum #N: applied ... fix(es)`, and the checkpoint after
   that produces no divergence message.

## Testing

Two layers, both driven from the in-game dev console (` key; debug commands are enabled whenever the game runs
modded). The command class is `Testing/HsrConsoleCmd.cs`, discovered automatically like RelicStats' command.
**Debug builds only** (`#if DEBUG`, same as RelicStats): `dotnet build -c Release` ships without `hsr`, so players
cannot drift their own state on purpose; use `-c Debug` for testing.

**Fake second player, no network: `hsr selftest`.** Works in any combat, singleplayer included. For every scenario
it snapshots the live state as the "host truth", perturbs the live state the way a drifted client would look
(energy, gold, run RNG, player RNG, enemy HP, block, power amount, pile order, extra card, potion, relic, orb,
choice ids, two identical cards swapping places), snapshots that as the client's own checkpoint, runs
`HostStateApplier.Apply` and asserts that the live state hashes back to the truth with the game's own
`ChecksumTracker.GenerateChecksum` AND that every combat card ID is back in its slot (`CardIdLayout`). The checksum
alone passed the broken v0.1.0 shuffle repair. A final scenario adds a lockstep action after the checkpoint (+1
energy) and checks the three-way merge keeps it. `hsr selftest energy` runs one scenario; failures print a line diff
of the two state dumps and of the two ID layouts. Subcommands and drift kinds tab-complete in the console.

**Real second player, local ENet: `tools/localmp.sh`.** The game ships a developer shortcut: `--fastmp=host_standard`
hosts over ENet on `127.0.0.1:33771` without Steam, `--fastmp=join --clientId=1001` joins it. The script launches
one host and N client instances of the installed game. Start a run, enter a fight, then on the client run
`hsr desync rng` (or any kind from `hsr list`). The command is not networked, so only that peer drifts; at the next
checkpoint the host log shows `diverged ... keeping it connected` and the client log shows
`[HostStateResync] checksum #N: applied ...`. `hsr status` prints strike counters and the last resync result.

Status 2026-09-24: `hsr selftest` was run in a live fight through the STS2 MCP console passthrough
(`GET localhost:15526/console?cmd=hsr%20selftest`) and passes every scenario; it caught one real bug on
the first run (card matching used `SerializableCard.Equals`, which ignores the floor-added field that
the checksum includes). The two-instance ENet path has not been run yet.

Status 2026-10-09: a real two-player Steam session ran the v0.1.0 test plan. Combined drifts (energy, gold, rng,
playerrng, hp, block) healed in one resync; the shuffle repair broke card IDs as described under "Which divergences
are fixable". v0.2.0 (card IDs) builds but has not been run in game yet.
