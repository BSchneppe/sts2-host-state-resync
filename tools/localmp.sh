#!/usr/bin/env bash
# Launches a local co-op session: one host and N client instances of the game on this machine, connected over
# ENet on 127.0.0.1:33771. This uses the game's own developer shortcut (--fastmp), which skips the Steam lobby.
#
#   tools/localmp.sh            # host + 1 client
#   tools/localmp.sh 2          # host + 2 clients
#
# Steam must be running. The game refuses to start outside Steam unless it is told its app ID, so each instance gets
# SteamAppId in its environment and a steam_appid.txt in its working directory. Steam cannot start a second copy of a
# running game, which is why the instances are started directly instead of through steam://.
#
# --fastmp runs without a Steam identity (player IDs 1, 1001, 1002, ...), so its saves go to
# <user data>/default/<id>/, apart from your Steam profiles.
#
# If the STS2_MCP mod is installed, the host gets its configured port (15526 by default) and client N gets that
# port + N, so each window can be driven separately; the config file is restored afterwards.
#
# Then pick characters in each window and embark, or with STS2_MCP:
#   curl -X POST localhost:15527/api/v1/singleplayer -d '{"action":"menu_select","option":"DEFECT"}'   (and "embark")
#   curl -G localhost:15526/console --data-urlencode "cmd=fight NIBBITS_WEAK"
# Open the dev console (` key) on a CLIENT window and run `divergence desync energy` (or rng, hp, shuffle, losecard,
# ...), then play a card. The host log shows "diverged ... keeping it connected" and the client log shows
# "[SlayTheDivergence] checksum #N: applied ...". `divergence selftest` needs no second instance; it runs in a
# singleplayer combat only.
set -euo pipefail
CLIENTS="${1:-1}"
[[ "$CLIENTS" =~ ^[1-9][0-9]*$ ]] || { echo "usage: $0 [number of clients, 1 or more]" >&2; exit 2; }
APP_ID=2868840
APP="${STS2_APP:-$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app}"
BIN="$APP/Contents/MacOS/Slay the Spire 2"
MCP_CONF="$APP/Contents/MacOS/mods/STS2_MCP/STS2_MCP.conf"
LOGDIR="${TMPDIR:-/tmp}/sts2-localmp"
mkdir -p "$LOGDIR"
echo "$APP_ID" > "$LOGDIR/steam_appid.txt"

MCP_PORT=""
if [[ -f "$MCP_CONF" ]]; then
  MCP_PORT="$( (grep -oE '"port"[^0-9]*[0-9]+' "$MCP_CONF" || true) | grep -oE '[0-9]+' | head -n1 || true)"
fi
if [[ -n "$MCP_PORT" ]]; then
  # A port that already answers belongs to a game that is still running; its readiness would be mistaken for ours.
  for i in $(seq 0 "$CLIENTS"); do
    if curl -s -m 1 "http://localhost:$((MCP_PORT + i))/api/v1/singleplayer" >/dev/null; then
      echo "port $((MCP_PORT + i)) is in use; quit the running game first" >&2
      exit 1
    fi
  done
  cp "$MCP_CONF" "$LOGDIR/STS2_MCP.conf.orig"
  trap 'cp "$LOGDIR/STS2_MCP.conf.orig" "$MCP_CONF"' EXIT
fi

# Starts one instance; with STS2_MCP, waits until it serves its port so the next instance can take the config file.
launch() {
  local name="$1" port="$2"; shift 2
  [[ -n "$port" ]] && printf '{\n  "port": %s\n}' "$port" > "$MCP_CONF"
  echo "$name -> $LOGDIR/$name.log${port:+ (STS2_MCP on :$port)}"
  (cd "$LOGDIR" && SteamAppId=$APP_ID SteamGameId=$APP_ID exec "$BIN" "$@" >"$LOGDIR/$name.log" 2>&1) &
  if [[ -n "$port" ]]; then
    for _ in $(seq 1 90); do
      curl -s -m 2 "http://localhost:$port/api/v1/singleplayer" >/dev/null && return 0
      kill -0 $! 2>/dev/null || { echo "$name exited during startup; see $LOGDIR/$name.log" >&2; return 1; }
      sleep 2
    done
    echo "$name did not open :$port within 3 minutes" >&2
    return 1
  fi
  sleep 8
}

launch host "$MCP_PORT" --fastmp=host_standard
for i in $(seq 1 "$CLIENTS"); do
  launch "client$i" "${MCP_PORT:+$((MCP_PORT + i))}" --fastmp=join --clientId=$((1000 + i))
done
# Every instance has read its port by now; put the config back so a normal launch gets the usual one.
[[ -n "$MCP_PORT" ]] && cp "$LOGDIR/STS2_MCP.conf.orig" "$MCP_CONF" && trap - EXIT
wait
