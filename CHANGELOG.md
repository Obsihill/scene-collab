# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.2.2] — 2026-10-31

Verified against the published image, not just a local build: a headless Unity editor and a
non-Unity WebSocket peer exchanged a cube move in both directions through
`ghcr.io/obsihill/scene-collab`.

### Fixed

- `/health` reported `serverVersion: 0.1.0` while the package was 0.2.x — the assembly version was
  never bumped.
- The container healthcheck ran `dotnet --info`, which passes as long as the .NET host exists. It now
  runs `--health-check`, a real GET to `/health` from inside the container (the runtime image has no
  curl or bash).
- `tools/live-run.sh` can point at an already-running server (`EXTERNAL_URL`/`EXTERNAL_TOKEN`) and
  passes native paths to Unity instead of MSYS ones, which Unity cannot open.

## [0.2.1] — 2026-10-31

Release plumbing only; no behaviour change in the editor.

### Fixed

- The tagged image never pushed: Docker rejects an uppercase repository name, and the workflow built
  `ghcr.io/Obsihill/...` from `github.repository_owner`. The image path is now the lowercase literal.
- Relayed ops now carry the author's `displayName`, so a receiver can label an edit without matching
  `clientId` against a roster it may have joined after.
- Arm64 image leg runs under QEMU; without it buildx fails on the second platform.

## [0.2.0] — 2026-10-31

Scene edits now travel. A cube moved in one editor appears moved in the other, in both directions.

### Added

- `SceneNodeRegistry` — `GlobalObjectId` ↔ short node id, persisted in a `<scene>.ids.json` sidecar
  (commit it; without it, ids are re-derived and a rename or reparent can hand the same object a new
  identity).
- `SceneValueCodec`, `ScenePropertyAccess`, `SceneDiff`, `SceneChangeFeed` — capture: the editor
  watches `ObjectChangeEvents`, reads the changed `SerializedProperty`, and emits `set_property` ops.
- `SceneOpApplier`, `SceneCollabSync` — apply: incoming ops are written through `SerializedProperty`
  with an echo guard so a remote edit never bounces back out.
- `SceneSaveGuard` — only the scribe saves the scene; the others get a warning instead of a conflicting
  write.
- `ISceneCollabSession.OperationAcknowledged` — the server's verdict on an op, so the scene layer can
  pace its outbox.
- `tools/SceneCollab.Remote` — a Unity-free participant that speaks the wire protocol, so an
  end-to-end run can prove the editor side instead of proving itself.
- `Tests/Editor/LiveHarness.cs` + `tools/live-run.sh` — one command runs server, headless editor and
  peer, and exits non-zero unless an edit moved in both directions.

### Changed

- `CollabSession` reads the access token and builds the connection URL on the main thread. Both touch
  editor state, and calling them from the socket task throws
  `GetString can only be called from the main thread` — which surfaced as a session that connected
  and then sat at revision 0.

### Known limits

- Structural ops (`add_node`, `remove_node`, `reparent`, `reorder_children`) are defined but not
  emitted yet: v0.2 syncs `m_LocalPosition`, `m_LocalRotation`, `m_LocalScale` and `m_Name`.
- Prefab instance overrides (`m_Modifications`) still need their own op path.

## [0.1.0] — 2026-10-31

First release: the connection layer, end to end.

### Added

- Wire protocol v1 (`WireMessage`, `MessageTypes`, `OpKinds`, `ErrorCodes`, `ProtocolVersion`)
  with a single source of truth inside the Unity package.
- `SceneCollab.Contracts` (.NET, netstandard2.1) that links those sources — no duplicated schema.
- `SceneCollab.Domain` (netstandard2.1): authoritative `RoomState` with revision counter,
  scribe election, per-participant presence, idempotent op replay and `oldValue` conflict
  rejection.
- `SceneCollab.Server.WebApi` (.NET 10, `Microsoft.NET.Sdk.Web`): raw WebSocket endpoint at `/ws`,
  `GET /health`, constant-time token check via `X-SceneCollab-Token`, room-name validation,
  fail-closed startup when no token is configured, loopback bind by default.
- Unity package `com.obsihill.scenecollab`: `SceneCollab.Contracts`, `SceneCollab.Domain`,
  `SceneCollab.Editor` assemblies, `Tools > Scene Collab` window, `CollabSession` (main-thread
  marshalled WebSocket client), `CollabSettings` (token in `EditorPrefs`, never in
  `ProjectSettings`), `StableNodeId`, `SceneOperation`.
- Tests: 16 xUnit tests for the room rules (including a parallel-apply race), 24 Unity EditMode
  tests for id rules, op validation and address normalisation.
- `tools/SceneCollab.Smoke`: two-client end-to-end check (roster, op relay, duplicate replay,
  bad-token rejection).
- Docker: multi-stage `Dockerfile`, `docker-compose.yml`, `.env.example`.
- CI: build + test, plus guards against duplicated protocol sources, absolute `file:` package
  paths and bundled Unity tooling.

### Notes

- Scene capture (`ObjectChangeEvents`) and op application are **not** in this release; the window
  connects, shows the roster and streams operations, but does not yet move scene objects.
- `UnityYAMLMerge.exe` is deliberately not bundled (Unity redistribution).
