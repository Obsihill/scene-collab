#!/usr/bin/env bash
# One-command live verification: server + headless Unity editor + a non-Unity peer.
#
# Two cubes move, one in each direction:
#   peer  -> editor  set m_LocalPosition = 3,4,5   (editor must show the cube at 3,4,5)
#   editor -> peer   set m_LocalPosition = 7,8,9   (peer must receive exactly that op)
#
# Every step leaves a JSON file in artifacts/live, so a failing run says which direction broke.
# Requires: .NET SDK 10, Unity 6000.6 (edit UNITY accordingly), a published server in artifacts/server.
#
#   dotnet publish server/SceneCollab.Server.WebApi -c Release -o artifacts/server
#   bash tools/live-run.sh
set -u
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT" || exit 1

UNITY="${UNITY:-/c/Program Files/Unity/Hub/Editor/6000.6.4f1/Editor/Unity.exe}"
PROBE="artifacts/unity-probe"
DIR=artifacts/live
TOKEN="livetest-$RANDOM$RANDOM"

if [ ! -x "$UNITY" ]; then
  echo "Unity not found at $UNITY (set UNITY=/path/to/Unity)"
  exit 1
fi

if [ ! -x artifacts/server/SceneCollab.Server.WebApi.exe ]; then
  echo "publishing server first"
  dotnet publish server/SceneCollab.Server.WebApi -c Release -o artifacts/server -v q --nologo || exit 1
fi

dotnet build tools/SceneCollab.Remote -v q --nologo || exit 1

rm -rf "$DIR"
mkdir -p "$DIR"

export SCENECOLLAB_TOKEN="$TOKEN"
export SCENECOLLAB_BIND=127.0.0.1
export SCENECOLLAB_PORT=8100

./artifacts/server/SceneCollab.Server.WebApi.exe > artifacts/live-server.log 2>&1 &
SERVER_PID=$!

health=""
for _ in $(seq 1 60); do
  health=$(curl -s -o /dev/null -w "%{http_code}" http://127.0.0.1:8100/health 2>/dev/null || true)
  [ "$health" = "200" ] && break
  sleep 0.5
done
echo "server_pid=$SERVER_PID health=$health"

if [ "$health" != "200" ]; then
  echo "server did not become healthy; see artifacts/live-server.log"
  kill $SERVER_PID 2>/dev/null
  exit 1
fi

dotnet tools/SceneCollab.Remote/bin/Debug/net10.0/SceneCollab.Remote.dll \
  --url ws://127.0.0.1:8100/ws --token "$TOKEN" --room live --dir "$DIR" --expect 7,8,9 \
  > artifacts/live-peer.log 2>&1 &
PEER_PID=$!

export SCENECOLLAB_LIVE_URL=ws://127.0.0.1:8100/ws
export SCENECOLLAB_LIVE_TOKEN="$TOKEN"
export SCENECOLLAB_LIVE_ROOM=live
export SCENECOLLAB_LIVE_DIR="$ROOT/$DIR"

"$UNITY" -batchmode -projectPath "$ROOT/$PROBE" \
  -executeMethod SceneCollab.Editor.Tests.LiveHarness.Run \
  -logFile "$ROOT/artifacts/live-unity.log"
UNITY_EXIT=$?

wait $PEER_PID
PEER_EXIT=$?

kill $SERVER_PID 2>/dev/null
wait $SERVER_PID 2>/dev/null

echo "unity_exit=$UNITY_EXIT peer_exit=$PEER_EXIT"
echo "--- artifacts ---"
ls -1 "$DIR" 2>/dev/null

if [ "$UNITY_EXIT" = "0" ] && [ "$PEER_EXIT" = "0" ]; then
  echo "PASS: edits moved in both directions"
  exit 0
fi

echo "FAIL: see artifacts/live-unity.log and artifacts/live-peer.log"
exit 1
