#!/usr/bin/env bash
# Does an editor that lost the server come back by itself?
#
# The first half is the ordinary live run (one real editor, one non-Unity peer, one edit each way).
# Then the shell pulls the server out from under the editor: the socket dies, every op the editor
# makes from then on would have gone nowhere, and before the reconnect work the only way out was to
# press Disconnect and Connect again — which is exactly the "Host 하다보면 나가네" report.
#
# Expectations:
#   * the editor notices the drop (state leaves Connected) without anyone touching it
#   * it reconnects on its own against the restarted server
#   * the next edit it makes is actually sent
#
#   dotnet publish server/SceneCollab.Server.WebApi -c Release -o artifacts/server
#   bash tools/live-reconnect.sh
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 1

ROOT_WIN="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"

UNITY="${UNITY:-/c/Program Files/Unity/Hub/Editor/6000.6.4f1/Editor/Unity.exe}"
PROBE="artifacts/unity-probe"
DIR=artifacts/live-reconnect
PORT="${EXTERNAL_PORT:-8102}"
TOKEN="reconnect-$RANDOM$RANDOM"
URL="ws://127.0.0.1:$PORT/ws"
ROOM="reconnect"
SERVER_LOG=artifacts/live-reconnect-server.log
SERVER_PID=""

if [ ! -x "$UNITY" ]; then
  echo "Unity not found at $UNITY (set UNITY=/path/to/Unity)"
  exit 1
fi

if [ ! -x artifacts/server/SceneCollab.Server.WebApi.exe ]; then
  dotnet publish server/SceneCollab.Server.WebApi -c Release -o artifacts/server -v q --nologo || exit 1
fi

dotnet build tools/SceneCollab.Remote -v q --nologo || exit 1

rm -rf "$DIR"
mkdir -p "$DIR"
: > "$SERVER_LOG"

start_server() {
  SCENECOLLAB_TOKEN="$TOKEN" SCENECOLLAB_BIND=127.0.0.1 SCENECOLLAB_PORT="$PORT" \
    ./artifacts/server/SceneCollab.Server.WebApi.exe >> "$SERVER_LOG" 2>&1 &
  SERVER_PID=$!
  local health=""
  for _ in $(seq 1 60); do
    health=$(curl -s -o /dev/null -w "%{http_code}" "http://127.0.0.1:$PORT/health" 2>/dev/null || true)
    [ "$health" = "200" ] && return 0
    sleep 0.5
  done
  return 1
}

start_server || { echo "server did not become healthy; see $SERVER_LOG"; exit 1; }
echo "server_pid=$SERVER_PID"

dotnet tools/SceneCollab.Remote/bin/Debug/net10.0/SceneCollab.Remote.dll \
  --url "$URL" --token "$TOKEN" --room "$ROOM" --dir "$DIR" --expect 7,8,9 \
  > artifacts/live-reconnect-peer.log 2>&1 &
PEER_PID=$!

export SCENECOLLAB_LIVE_URL="$URL"
export SCENECOLLAB_LIVE_TOKEN="$TOKEN"
export SCENECOLLAB_LIVE_ROOM="$ROOM"
export SCENECOLLAB_LIVE_DIR="$ROOT_WIN/$DIR"
export SCENECOLLAB_LIVE_RECONNECT=1

"$UNITY" -batchmode -projectPath "$ROOT_WIN/$PROBE" \
  -executeMethod SceneCollab.Editor.Tests.LiveHarness.Run \
  -logFile "$ROOT_WIN/artifacts/live-reconnect-unity.log" &
UNITY_PID=$!

# The harness writes unity-result.json once the first exchange has landed in both directions.
for _ in $(seq 1 240); do
  [ -f "$DIR/unity-result.json" ] && break
  sleep 0.5
done

if [ ! -f "$DIR/unity-result.json" ]; then
  echo "the first exchange never completed; see artifacts/live-reconnect-unity.log"
else
  sleep 2
  echo "killing the server under the editor"
  kill "$SERVER_PID" 2>/dev/null
  wait "$SERVER_PID" 2>/dev/null
  SERVER_PID=""

  # Long enough for the editor to fail a few retries, not long enough to exhaust the ladder.
  sleep 10
  echo "bringing the server back"
  start_server || echo "server did not come back"
fi

wait "$UNITY_PID"
UNITY_EXIT=$?
wait "$PEER_PID" 2>/dev/null
PEER_EXIT=$?

if [ -n "$SERVER_PID" ]; then
  kill "$SERVER_PID" 2>/dev/null
  wait "$SERVER_PID" 2>/dev/null
fi

echo "unity_exit=$UNITY_EXIT peer_exit=$PEER_EXIT"
echo "--- artifacts ---"
ls -1 "$DIR" 2>/dev/null

if [ "$UNITY_EXIT" = "0" ] && [ -f "$DIR/unity-reconnect.json" ]; then
  echo "PASS: the editor dropped, reconnected on its own, and kept editing"
  exit 0
fi

echo "FAIL: see artifacts/live-reconnect-unity.log and $SERVER_LOG"
exit 1
