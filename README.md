# Scene Collab for Unity

Multiple people editing **the same Unity scene at the same time**, without giving up Git.

```
Git stays the source of truth. One editor (the scribe) writes the .unity file.
Everyone else streams typed operations to the authoritative server.
```

- No accounts, no cloud service, no per-seat licence. Self-host with one Docker command.
- Editor-only UPM package. Your game build is untouched.
- Design rationale and limits: [`docs/design.md`](docs/design.md). Wire protocol: [`docs/protocol.md`](docs/protocol.md).
- 한국어 빠른 시작: [`docs/quickstart.ko.md`](docs/quickstart.ko.md)

---

## Status

v0.3 turns a connection into a **session**: one editor opens a scene and hosts it, everyone else picks
that scene from a list, and only the host writes the file. Structural edits travel as well — objects
appear, move in the hierarchy and disappear on every machine — and an object under restructure is
locked so two people cannot fight over the same GameObject.

| Part | State |
|---|---|
| Server: `/health`, `/rooms`, token auth, room isolation, presence, host promotion | done |
| Operations: authoritative apply, broadcast, idempotent replay, conflict rejection, node locks | done (24 unit tests + probe) |
| Unity package: install, window, scene picker, connect, presence, lock UI | done (36 EditMode tests, Unity 6000.6) |
| Value ops (`set_property`): transform, name, active, enabled, component fields | done |
| Structural ops: `add_node`, `remove_node`, `reparent`, `reorder_children`, `add_component` | done (host-only, lock-guarded) |
| Asset manifest + placeholders for missing assets | done |
| Late joiner: host streams the full scene as a chunked snapshot | done |
| Prefab instance overrides (`set_prefab_override` promotion) | done |
| Peer scene-view cameras (editor overlay, `view` messages) | done |
| Terrain heightmap + splatmap (host-authoritative region patches) | done (v0.5) |
| Prefab-instance structural edits (`addedObjects`), terrain trees/details/holes | not yet — see [roadmap](#roadmap) |

Verified end to end, not by unit tests alone:

- `tools/SceneCollab.V03Probe` — 23 checks against a live server with two WebSocket peers, no editor.
- `LiveHarnessV03` — a Unity-free peer creates a cube in a headless Unity scene, moves it to
  `(2, 3, 4)`, then deletes it; the editor asserts the object, its position, the node-id binding and
  its removal.
- `tools/live-run.sh` — value edits move in both directions.
- `tools/SceneCollab.V043Probe` — 19 checks against a live server: snapshot start/chunk/end relayed
  with the `snapshot` flag, a host snapshot allowed through a guest's lock while a guest's "snapshot"
  is still refused (`locked`), and a camera pose delivered to the peer but not echoed to its sender and
  without moving the revision.
- 28 server tests, 62 EditMode tests.

## 1. Install the Unity package

`Packages/manifest.json`:

```jsonc
"com.obsihill.scenecollab": "https://github.com/Obsihill/scene-collab.git?path=unity/com.obsihill.scenecollab#v0.5.0"
```

Then `Tools > Scene Collab`. Requires Unity **6000.0 or newer**.

## 2. Start the server

```bash
cp .env.example .env      # set SCENECOLLAB_TOKEN to any long random string
docker compose up -d
curl http://localhost:8080/health
```

The server **refuses to start without a token** and binds to `127.0.0.1` by default. Put it
behind a TLS-terminating proxy for anything beyond your LAN, and forward WebSocket upgrades to
`:8080`.

## 3. Connect

Window → server URL (`ws://localhost:8080/ws`) → access token → **Refresh** the scene list → pick the
scene someone already has open → **Connect**. If nobody has opened a scene yet, type a room name and
connect first: **the first client hosts that scene**, everyone after it joins.

The host is also the scribe: it saves the scene to disk. If the host disconnects, the participant who
has been connected longest takes over, so saving does not stop. Everyone else's `Ctrl+S` is blocked
with a warning rather than writing a diverging file.

---

### Quickstart scene

Package Manager → **Scene Collab** → *Samples* → **Quickstart Scene** → *Import* gives you a
two-cube scene to try the session on. It contains no project assets on purpose.

## What syncs, and what does not

The honest version, by feature. "Lock-guarded" means the op is refused while another participant holds
that node's lock. Any participant may restructure — but only the host can save the scene file.

| Edit | Syncs? | Notes |
|---|---|---|
| Transform position / rotation / scale | yes | per channel, not per vector |
| GameObject name, `activeSelf` | yes | |
| Component `enabled` | yes | |
| Component fields: `int`, `float`, `bool`, `string`, `enum`, `Vector2`, `Vector3`, `Quaternion`, `Color` | yes | any component, found by walking `SerializedProperty` |
| `Vector4`, `Vector2Int`, `Vector3Int`, `Rect`, `RectInt`, `Bounds`, `BoundsInt`, `LayerMask` | yes | exact round-trip, no rounding |
| Small scalar arrays (`float[]`, `int[]`, `bool[]`, `string[]`, `Vector2/3/4[]`, `Color[]`, enums) | yes | up to 64 elements, travels as one value, resizing included |
| Arrays past 64 elements, empty arrays | **no** | nothing to compare and no bound on the payload |
| Object/asset reference fields, `AnimationCurve`, `Gradient` | **no** | unsupported types are skipped silently on the sender |
| Create / delete a GameObject | yes | any participant; lock-guarded |
| Reparent, reorder children | yes | lock-guarded |
| Add a component | yes | lock-guarded; a type this project lacks leaves a labelled stand-in |
| Remove a component | yes | lock-guarded, replay-safe |
| Prefab instance field overrides (`m_Modifications`) | yes | sent as `set_prefab_override`, applied as one, pinned with `RecordPrefabInstancePropertyModifications`; the array order is not normalised |
| Restoring a field to the prefab's default | **no** | the override is removed locally without an op, so a peer keeps the old override |
| Joining after the session started | yes | the host streams its whole scene as a snapshot; the joiner ends up with the host's scene, not a merge |
| Add / remove a component *of a prefab instance* | **no** | the instance's `addedObjects` list is not carried |
| Terrain heightmap / splatmap | **yes**, host-only | stroke-sized region patches on their own channel: 16-bit height tiles, byte splat tiles; a guest whose resolution or layer count differs refuses the patch and says so |
| Terrain trees / details / holes | **no** | not on the channel yet |
| Materials and other **assets** | **no** | shared through Git, by design |
| Lightmap bake, NavMesh bake, terrain LOD | **no** | host bakes and saves, then Git |
| More than one scene per session | **no** | one active scene; a different scene GUID is refused |
| Undo / redo | **no** | each editor's undo stack stays local |
| Saving the scene | **host only** | other editors' saves are blocked with a warning |
| Selection, cursor, inspector focus | **no** | not modelled |
| Peer camera positions | yes, as an overlay | `view` messages draw a camera gizmo in the scene view; nothing is written to the scene |

Two participants editing the **same value** resolve last-writer-wins. Two participants restructuring
the **same object** is prevented by the lock, not resolved after the fact.

## What this is not

| Not supported | Why |
|---|---|
| Editing one scene file simultaneously on disk | `SaveScene` rewrites the whole YAML and reassigns fileIDs — file-level merge cannot work |
| Prefab **asset** live editing | Assets are not scene objects; this covers scene instances only |
| Simultaneous editing of different scenes | A session is bound to one scene GUID |
| Working without the host online | The server holds the operation log; there is no offline mode |
| 100k-line monolithic scenes | Operation latency and resync cost; use additive scene splitting |
| Working offline | The server is authoritative; there is no local op queue yet |

## Repository layout

```
contracts/    SceneCollab.Contracts.csproj   (links the C# sources that live in the Unity package)
server/       SceneCollab.Domain / .Server.WebApi
tests/        SceneCollab.Domain.Tests
tools/        SceneCollab.Smoke              (manual end-to-end WS client)
unity/        com.obsihill.scenecollab        (UPM package, the shipped artifact)
docs/         protocol + design + quickstart.ko
```

**Single source of truth for the wire protocol:** `unity/com.obsihill.scenecollab/Contracts/`.
The `contracts` project compiles those same files with `<Compile Include ... />`; there is no copy.
CI fails if a second copy appears anywhere.

## Building and testing

```bash
dotnet build SceneCollab.slnx
dotnet test  SceneCollab.slnx
```

End-to-end check against a running server:

```bash
dotnet run --project tools/SceneCollab.Smoke -- --url ws://127.0.0.1:8080/ws --token <token>
```

It opens two clients, asserts the roster, sends one op, asserts the relay and the idempotent
replay, then checks that a wrong token is rejected.

The real end-to-end check — server, headless Unity editor and a Unity-free peer, with a cube moved in
both directions:

```bash
bash tools/live-run.sh          # UNITY=/path/to/Unity if it is not in the default Hub location
```

It publishes the server, starts it on `127.0.0.1:8100` with a throwaway token, and exits `0` only if
the peer's move landed in the editor **and** the editor's move came back to the peer. Each stage
writes a JSON file under `artifacts/live/`, so a failure names the direction that broke. Point it at
an already-running server with `EXTERNAL_URL=...` / `EXTERNAL_TOKEN=...` — that is how the shipped
container image is checked:

```bash
EXTERNAL_URL=ws://127.0.0.1:8102/ws EXTERNAL_TOKEN=<token> bash tools/live-run.sh
```

Unity package tests run from any project that references the package; add it to `testables`
in `Packages/manifest.json`:

```jsonc
"testables": ["com.obsihill.scenecollab"]
```

## Roadmap

1. **v0.1** — connection layer: protocol, server, package, tests.
2. **v0.2** — scene capture and apply for transform/name/active/enabled properties; scribe save.
3. **v0.3** — scene-level sessions: `/rooms` scene list, scene-GUID handshake, host = scribe, host
   promotion, structural ops under object lock, component adds, asset manifest and placeholders.
4. **v0.4** — component removal, missing-script stand-ins, prefab field overrides. ✅
5. **v0.4.6** — a guest that reconnects re-requests its snapshot. ✅
6. **v0.4.7** — an object arrives whole: components by slot, references by asset GUID / node id,
   sibling order preserved, and missing content shows a GMod-style ERROR mesh. ✅
6. **v0.4.5** — 20 s heartbeats both ways, dead peers hung up on after 60 s, automatic reconnect. ✅
7. **v0.4.4** — abandoned rooms are dropped when the last participant leaves. ✅
8. **v0.4.3** — late-joiner snapshot, prefab override promotion, peer scene-view cameras.
6. **v0.5** — terrain: heightmap and splatmap region deltas over a dedicated channel. ✅ (trees,
   details, holes and a terrain lock are still open)
7. **v0.5.x** — `samples~` demo scene, prefab-instance structural edits (`addedObjects`).
7. **v1.0** — first-run wizard (Lv1 host mode: server spawned locally, 6-digit code).

## License

MIT — see [`LICENSE`](LICENSE). `UnityYAMLMerge` is **not** bundled: it is Unity's redistribution
tool and lives in your Unity installation (`Editor/Data/Tools/`).
