#!/usr/bin/env bash
# Launches a Steam-free local co-op session: one host and one client instance of the game on this machine,
# connected over ENet on 127.0.0.1:33771. This uses the game's own developer shortcut (--fastmp), no Steam needed.
#
#   tools/localmp.sh            # host + 1 client
#   tools/localmp.sh 2          # host + 2 clients
#
# Then in the host: pick characters, start the run, walk into a fight. Open the dev console (` key) on the CLIENT
# window and run `hsr desync rng` (or energy, hp, shuffle, ...). At the next checkpoint the host log shows
# "diverged ... keeping it connected" and the client log shows "[HostStateResync] checksum #N: applied ...".
# `hsr selftest` works in any combat, singleplayer included, and needs no second instance at all.
#
# Both instances share the same save directory; that is fine for a throwaway test run.
set -euo pipefail
CLIENTS="${1:-1}"
APP="${STS2_APP:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app}"
BIN="$APP/Contents/MacOS/Slay the Spire 2"
LOGDIR="${TMPDIR:-/tmp}/sts2-localmp"
mkdir -p "$LOGDIR"

echo "host  -> $LOGDIR/host.log"
"$BIN" --fastmp=host_standard >"$LOGDIR/host.log" 2>&1 &
sleep 8
for i in $(seq 1 "$CLIENTS"); do
  echo "client $i -> $LOGDIR/client$i.log"
  "$BIN" --fastmp=join --clientId=$((1000 + i)) >"$LOGDIR/client$i.log" 2>&1 &
  sleep 3
done
wait
