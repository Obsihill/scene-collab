# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.4.5] — 2026-11-01

### Added

- **Connections that die quietly are noticed, and picked back up.** Both ends now send a protocol-level
  `ping` every 20 s and answer it with `pong`; a peer that has been silent for 60 s is hung up on.
  TCP is not enough here — behind a reverse proxy the upstream socket stays open for hours after the
  editor behind it slept or lost Wi-Fi, so the server believed the room was still occupied, `/health`
  counted a room nobody was in, and the editor believed it was still connected while every op it sent
  went nowhere. The editor now also reconnects on its own: 1 s, 2 s, 4 s, 8 s, 15 s, then every 30 s,
  with a successful connection resetting the ladder, and a guest that comes back asks the host for a
  snapshot again so it is not left editing a stale scene.

## [0.4.4] — 2026-11-01

### Fixed

- **Abandoned rooms are dropped.** A room was created on the first join and never removed, so every
  room name anyone ever typed stayed in the process for the lifetime of the container: `/health`
  reported rooms that `/rooms` did not list, and the count only reset when the container was
  restarted. The last participant to leave now hands the room back to the hub, which removes it —
  only if the registered instance is still the one that went empty, so a re-created room with the
  same key is never evicted by a late teardown.

## [0.4.3] — 2026-11-01

### Added

- **Late joiners get the host's scene.** Until now a participant who joined after the edits started
  saw an empty scene: the server holds the op log, not the scene, and nothing replayed it. The host
  now diffs on demand — `snapshot_start` → per-node `add_node` / `add_component` / `set_property` /
  `set_prefab_override` → `snapshot_end` (node count) — and streams it in chunks, one chunk in flight
  at a time. Nodes the joiner holds that the host does not are removed, so the result is the host's
  scene, not a union. Local diffing is suppressed while a snapshot is being applied, and a snapshot
  never carries `oldValue`: the host is stating what the scene *is*, not what changed.
- **Peer scene-view cameras.** Each editor publishes its `SceneView` pose (`view`, ~10 Hz, only when
  the camera actually moved) and every other editor draws it as a small camera body with a frustum
  and a name label, tinted with that participant's presence colour. It is an editor overlay
  (`HideFlags.DontSave`), never a scene object, never saved, never an op. `view` messages skip the
  revision and the op log entirely. Toggle: **Show peer cameras** in the window. A camera that stops
  publishing disappears after 5 s.
- **Prefab instance fields now survive the round trip.** A `set_property` on an object that belongs
  to a prefab instance is sent as `set_prefab_override` instead, which is the only path that ends in
  `RecordPrefabInstancePropertyModifications` — that is what keeps the override from being dropped
  when the receiver saves.

### Fixed

- **Adding a root-level object threw on the receiver.** `ApplyAddNode` read
  `created.transform.parent.childCount` to restore the sibling index; a root object has no parent, so
  every `add_node` of a top-level object failed with a `NullReferenceException` on the receiving
  editor. Guarded — this is also why objects created at the scene root did not appear on peers.
- **`set_property` on a prefab instance reached the receiver as a plain scene property.** See above;
  the receiver's `RecordPrefabInstancePropertyModifications` call was unreachable in practice.

### Changed

- A **host** snapshot is exempt from object locks (a guest holding a lock on one node must not leave
  the late joiner with a half-built scene). The exemption is host-only: a guest cannot mark its ops as
  `snapshot` to skip a lock.
- `op` broadcast frames carry `accepted: true` and a `snapshot` flag.

## [0.4.2] — 2026-11-01

### Fixed

- **Structural edits never left the editor.** `SceneChangeFeed.Drain` cleared the
  `StructureDirty` flag before the sync tick read it, so the sweep that turns hierarchy changes
  into `add_node` / `remove_node` / `reparent` / `reorder_children` / `add_component` /
  `remove_component` ops ran zero times. Only `set_property` ever reached the room: moving an
  existing object synced, creating or deleting one did not. The drain now hands the flag back to
  the caller and consumes it once.
- **A created object arrived as an empty Transform.** The `add_node` payload carries only name,
  active state and transform, so components travel as their own `add_component` ops. The sender
  baselined the component signature at creation time, which told the next sweep there was nothing
  to send — the peer got an invisible object where a cube should be. Non-prefab creations now
  start with an empty component baseline, so `MeshFilter` / `MeshRenderer` / `Collider` follow the
  add op in the same sweep.

## [0.4.1] — 2026-10-31

### Added

- **Wider value codec.** `Vector4`, `Vector2Int`, `Vector3Int`, `Rect`, `RectInt`, `Bounds`,
  `BoundsInt` and `LayerMask` now round-trip exactly. `Bounds` travels as centre + extents and comes
  back as size.
- **Small scalar arrays travel as one value** — `float[]`, `int[]`, `bool[]`, `string[]`,
  `Vector2[]`, `Vector3[]`, `Vector4[]`, `Color[]` and enums, up to 64 elements. Wire form is
  `count;element;element…` with `%` and `;` escaped inside elements, so resizing an array syncs too.
- **Quickstart sample** (`Samples~/Quickstart`): two cubes and a light, no project assets.

### Fixed

- **`docker compose up -d` never answered a request.** `.env` set `SCENECOLLAB_PORT` for the host
  side, but `env_file` passed the same value into the container, so the app listened on that port
  inside the container while the mapping pointed at 8080. Compose now pins the container port to
  8080 and `.env` only picks the host port. Verified: container `healthy`, `/health` on the host
  port, 26-check protocol probe PASS against the compose deployment.

### Notes

- Arrays past 64 elements and empty arrays stay untracked — nothing to compare, no bound on payload.
- Object/asset reference fields, `AnimationCurve` and `Gradient` remain unsynced.

## [0.4.0] — 2026-10-31

Components and prefab overrides. v0.3 could add a component but never remove one, and a property
edit on a prefab instance could land on the instance and still vanish at save time.

### Added

- **`remove_component`.** Relayed like the other structural kinds (lock-checked, `componentType` on
  the echo), applied with `DestroyImmediate` so it works in batch mode. Removing a component twice is
  applied instead of failed, so a replay after a reconnect does not abort the session.
- **Missing scripts leave a stand-in.** `add_component` for a type this project does not have used to
  be dropped with a note; it now attaches a labelled magenta marker naming the missing type, the same
  way missing meshes and prefabs are already handled.
- **`set_prefab_override`** is implemented. The property is written, then
  `PrefabUtility.RecordPrefabInstancePropertyModifications` pins it into the instance's modification
  list — writing through `SerializedProperty` alone can leave the value on the instance without an
  override, which a save then throws away.

### Fixed

- **Component add and delete were never captured.** Neither fires a property modification nor a
  hierarchy change, so a locally added or deleted component stayed local. A per-object component
  signature is now compared on every structural sweep. The signature keeps duplicates: Unity allows
  two colliders on one object, and a set would have reported that as no change.

### Verified

- Unity EditMode: **44/44** (was 36/36). Eight new cases cover component capture, remove, replay,
  missing-script stand-ins and prefab override recording.
- Probe over a live server: **26 checks** (was 23), including `remove_component` relay and lock.
- Unity ↔ non-Unity round trip: a `.NET` guest adds a component to the host's scene, the host applies
  it, the guest removes it, the host applies that too (`v03-host-comp.json`, `v03-host-comp-removed.json`).


## [0.3.0] — 2026-10-31

Scene-level multiplayer. One editor hosts a scene, everyone else joins *that* scene by picking it
from a list, and the host alone writes the file.

### Added

- **`GET /rooms`** lists the scenes currently open in a session: scene name, host name, participant
  count. Token-authenticated; a missing or wrong token is a 401.
- **Scene handshake.** `welcome` carries `sceneGuid`/`sceneName` and every participant now knows
  whose scene it is. A client joining a session with a different scene GUID is refused with
  `scene_mismatch` instead of silently editing a scene nobody else has.
- **Structural operations**: `add_node`, `remove_node`, `reparent`, `reorder_children`,
  `add_component`. The server relays them with `parentNodeId`, `siblingIndex`, `componentType` and
  `prefabGuid` intact; the receiving editor rebuilds the object and binds the remote-issued node id
  to it (`SceneNodeRegistry.BindExternalId`), so later edits on that object address it correctly.
- **Node locks.** Any participant can claim a node id, and the server echoes the whole lock set to
  everyone. A structural op on a locked node from someone else is rejected with `locked` rather than
  applied.
- **Asset manifest.** The host sends the scene's dependency GUID list; each client compares it with
  its own `AssetDatabase` and reports what it is missing, by GUID, instead of guessing from a count.
- **Host promotion.** When the host leaves, the participant who has been connected longest becomes
  the new host and scribe, so saving continues without a manual re-host.
- **Scene picker in the window** (`Editor/CollabRoomList.cs`): fetches `/rooms` over HTTP, shows the
  open scenes, and connects to the selected one.
- **Placeholder objects** (`CollabPlaceholders.cs`) for assets a client does not have: missing meshes
  get a stand-in cube with a magenta material (Unity draws a mesh-less renderer as nothing at all),
  missing prefabs get a transform stand-in, missing scripts get a marker component. All temporary
  (`HideFlags.DontSave`).

### Changed

- The first participant is the host *and* the scribe; the scribe flag is no longer independent.
- Structural ops are only accepted from the host. Everyone can still edit values.

### Verified

- `tools/SceneCollab.V03Probe` (new, .NET, no editor): 23 checks against a live server — room
  listing and 401, host/scribe assignment, scene-GUID refusal, structural relay including
  `parentNodeId`/`siblingIndex`/`componentType`/`prefabGuid`, lock broadcast to all participants,
  non-host structural rejection, asset GUID delivery, host promotion. Three consecutive passes.
- `LiveHarnessV03` + the probe's guest mode: a non-Unity peer added a `Guest Cube`, moved it to
  `(2, 3, 4)` and removed it, and a headless Unity host applied all three — object present at that
  position, node id bound, object gone afterwards.

### Known gaps

- Prefab instances are created and destroyed, but per-instance `m_Modifications` are not normalised;
  an edit to a single prefab instance field is not replayed as an override diff yet.
- `TerrainData` (heightmap, splatmap, trees, details) is not synchronised — terrain layers stay
  host-only.
- Lightmap baking, NavMesh baking and terrain LOD are host-side operations by design.

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
