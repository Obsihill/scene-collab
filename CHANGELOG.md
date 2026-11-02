# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.5.4] — 2026-11-02

### Changed

- **The default stand-in is a painted ERROR sign, not a magenta cube.** The cube was debug art that
  survived into releases: it marked *something* as wrong without saying what, and a bare magenta box in a
  level reads as a missing prop. What you get now is the sign the idea comes from — magenta and black
  chequerboard, the word ERROR in red with a dark outline — drawn from a texture the package generates at
  runtime, so no third-party art ships and the MIT licence stays clean. Two boards are crossed at right
  angles and textured on both sides: four faces, so the sign reads from any direction and never turns to
  face the camera (v0.5.1 removed camera-facing labels, and that decision stands).
- **The lettering keeps its 2:1 shape whatever the object's proportions.** A flat slab or a tall pillar
  would otherwise stretch the board into an unreadable smear, so the faces compensate against the bounds
  the marker is scaled to.
- `Tools ▸ Scene Collab ▸ ERROR model` still wins when it is set — a project with its own placeholder
  prefab (a scanned Half-Life style sign, say) is unaffected. The slot now means *instead of* the built-in
  sign rather than *instead of a cube*.

## [0.5.3] — 2026-11-02

### Fixed

- **ERROR markers retire when the thing they complained about arrives.** Marking used to be one-way: a
  marker hung up while a prefab was still importing (or before this project's asset database knew its
  guid) stayed in the scene after the asset arrived, so a prefab that synchronised perfectly still carried
  a magenta ERROR sign. Markers are now re-checked once a second against their own reason — a prefab or
  reference guid that resolves, a script type that loads, a renderer whose mesh and material are back — and
  removed the moment the reason no longer holds. Orphans of objects that no longer exist go too.
- **The health scan no longer runs mid-compile or mid-import.** In that window every script looks missing
  and every freshly pulled prefab looks empty, which is precisely when false markers were being created.
  It also only judges `MeshRenderer`/`SkinnedMeshRenderer`, because a particle or trail renderer without a
  material is a legitimate state, not a broken checkout.
- **A stand-in is replaced by the real prefab when the asset arrives.** Previously nothing converted it:
  the object stayed an empty GameObject holding the host's transform. If the stand-in is still untouched
  (no children of its own) it is now swapped for an instance of the prefab, keeping its node id, name,
  hierarchy position and transform; a stand-in that grew content is left alone so nobody's work is thrown
  away.

### Added

- **Reference markers remember the sub-asset id.** A `ref:<guid>:<fileId>` token names both halves, so a
  marker whose guid resolves but whose sub-asset was re-authored away keeps its sign instead of quietly
  disappearing — retiring on the guid alone would hide the very failure markers exist to show.

## [0.5.2] — 2026-11-02

### Fixed

- **ERROR markers are scene roots.** They used to be parented to the object they stand in for, so a
  parent with a non-uniform scale (a stretched prop, a thin trigger) stretched and displaced the marker:
  the sign saying "here is what you are missing" pointed at the wrong place at the wrong size. Markers are
  now top-level objects sized in world units, placed on the missing object's bounds, and tracked against
  their owner (`CollabPlaceholders.For`) instead of found by walking children. Clearing a resolved field
  still removes exactly that one marker.

### Added

- **`ERROR model` setting** (`Tools ▸ Scene Collab ▸ ERROR model`): a project prefab drawn instead of the
  built-in magenta cube, e.g. a Half-Life style ERROR sign. Stored as an asset path in
  `ProjectSettings/SceneCollabSettings.json`. The package ships no third-party art — Valve's error model is
  Valve's, so it stays in your project. Without it you get the emissive cube as before.

## [0.5.1] — 2026-11-02

### Changed

- **ERROR markers no longer move or follow the camera.** The floating `TextMesh` label is gone, and with
  it the slow turn, the bob and the camera-facing billboard: an annotation that tracks your view is noise
  exactly while you are trying to work. What stays is the part that does the job — a static magenta cube,
  emission on, sitting where the missing object would be. The reason moved to where it is not in the way:
  the marker's name in the hierarchy (`__collab_missing__ missing prefab: …`, `missing script: …`,
  `missing ref: …`) and the window's asset report, which now points at it.
- The editor no longer pays an `EditorApplication.update` callback for placeholder animation; a marker is
  created once and left alone.

## [0.5.0] — 2026-11-02

### Added

- **Terrain heights and splat weights sync.** Sculpting or painting a terrain used to sync nothing at
  all — and it was not a bug in the diff, it was the diff being right: heights and splat weights live in
  a `TerrainData` **asset**, and the scene component only holds a reference to it, so a sculpt changes no
  scene field and produces no op. Terrain now travels on its own `terrain` channel as stroke-sized region
  patches: a 128×128 tile of 16-bit heights (32 KB) or a 64×64 tile of per-layer byte weights, shrunk
  automatically when a terrain carries many layers. A patch never enters the op log, never consumes a
  revision, and is superseded by the next one.
- **The host is the only sculptor.** The server answers `not_host` to anyone else, matching the rule
  that the host owns the asset exactly as it owns the scene: a guest applies the patch, marks the
  TerrainData dirty, and never saves it. Two editors both sculpting a copy they cannot both persist
  would otherwise overwrite each other forever.
- **A mismatched terrain refuses the patch.** A receiver whose heightmap resolution, alphamap resolution
  or layer count differs reports it in the window instead of writing 1024 samples into a 512-sample
  layer — that corrupts the asset rather than degrading it.

### Not in this release

- Terrain **trees**, **details** and **holes** are not on the channel yet.
- No terrain lock: the host is the only writer, so there is nothing to lock.

## [0.4.7] — 2026-11-01

### Added

- **An object arrives whole.** Creating or deleting an object, parenting it, naming it, enabling it and
  moving it all travel as one ordered sequence: the object and its components exist first, then their
  fields are written, and only then are references resolved. The old single pass wrote fields against a
  half-built object, which is why a peer sometimes saw a correctly shaped but empty GameObject.
- **Components are identified by type *and* instance slot.** The wire carries `componentIndex`, and
  property paths read `c/UnityEngine.AudioSource#1/m_Volume`. Two AudioSources on one object used to be
  indistinguishable — both peers read and wrote slot 0, so the second one's settings were silently
  overwritten. An object that gains a component now sends every field of that component, not just the
  ones that differ from the type defaults.
- **References are synced.** A material, mesh, prefab or scriptable object travels as
  `ref:<guid>:<fileId>`; a scene object travels as `node:<nodeId>` and a component as
  `node:<nodeId>#<type>`. A field pointing at another object in the scene now survives the trip instead
  of arriving null.
- **Missing content is loud.** A reference that cannot be resolved on this machine leaves a GMod-style
  **ERROR marker** — an emissive magenta cube that spins above the object, labelled with the field — and
  is counted as `Unresolved refs` in the window. Values the codec cannot express are counted as
  `Unsupported values`. Neither is silent any more, and both clear themselves when a later write lands.
- **Sibling order is part of the data.** Every node op carries the parent's sibling index, root objects
  included, and the snapshot is emitted stable, parent-first, in sibling order — so the live path and
  the late-joiner path produce the same hierarchy instead of one arriving alphabetically.
- **Prefab instances are rebuilt from their GUID.** An object whose prefab is missing shows the ERROR
  mesh and a "missing prefab" label; a prefab instance that is present is instantiated first and its
  overrides applied afterwards, so the override values are not lost to instantiation.

### Fixed

- **A busy editor no longer loses its seat.** Pong frames are written from the socket's own thread
  instead of being queued for the editor's main thread. A minute spent saving a large scene, importing
  assets or entering play mode used to read as silence on the server, which then hung up and forced a
  reconnect; if the gap outlasted the timeout, the room emptied and a peer had to rejoin. The live
  harness reproduces the failure — a 73 s project load dropped *both* participants — and passes now.

## [0.4.6] — 2026-11-01

### Fixed

- **A guest that reconnects asks for a snapshot again.** The "asked once" flag survived the
  disconnect, so an editor that came back was a new join as far as the server was concerned — it had
  no baseline any more — but the re-request was skipped and it carried on editing a scene nobody else
  could see. Hosts were unaffected: they have no baseline to ask for.

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
