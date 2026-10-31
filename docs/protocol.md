# Wire protocol (v1)

One message shape, both directions. Everything below is a field of
`SceneCollab.Contracts.Protocol.WireMessage` — a flat class of public fields, because Unity's
`JsonUtility` can serialise exactly that and nothing more. No inheritance, no dictionaries,
no polymorphic payloads: that shape is also the shape that makes deserialising untrusted input
safe.

**Source of truth:** `unity/com.obsihill.scenecollab/Contracts/Protocol/`. The .NET server
compiles those same files through a `<Compile Include>` link; there is no second copy.

---

## Transport

```
WS  /ws?room=<room>          header: X-SceneCollab-Token: <token>
GET /health                  → JSON, unauthenticated on purpose (load balancer probes)
GET /rooms                   header: X-SceneCollab-Token  → JSON, scenes currently hosted
```

- Text frames, UTF-8, one JSON object per frame.
- Max inbound message: 4 MiB.
- Server pings with WebSocket keep-alive every 20 s.
- Room names: `^[A-Za-z0-9._-]{1,64}$`.

## Handshake

```
client → hello   { type, protocolVersion, displayName, room, sceneGuid, sceneName, scenePath }
server → welcome { type, clientId, displayName, color, room, revision,
                    isHost, isScribe, sceneGuid, sceneName, scenePath, participants[] }
server → presence (broadcast to the others; the joiner already has the roster in welcome)
```

The first participant of a room **is** the host: the server records it at join time and sends it
`isHost: true, isScribe: true`. Everyone after that joins the scene the room already has.

`sceneGuid` is the scene the client currently has open. If the room already holds a different
GUID, the join is refused with `scene_mismatch` — a client with a different scene open must not
be able to edit a document it does not have. If the room is empty, the joiner's scene becomes the
room's scene and is echoed in `welcome`.

When the host disconnects, the server promotes the participant that has been connected longest and
sends `welcome` to everyone with the new flags, so the host identity is never in doubt on any peer.

The server rejects an incompatible `protocolVersion` **before** any room state exists, then
closes the socket with `policy_violation`. Failing early is the point: a client that speaks a
different version must not be able to half-join and corrupt a room.

## Operations

```
client → op { type, opId, clientId, revision, opKind, nodeId, propertyPath, oldValue, newValue,
              parentNodeId, siblingIndex, componentType, prefabGuid }
server → ack { type, opId, accepted, duplicate, revision, code?, message? }
server → op  (broadcast to every other client in the room)
```

`opId` is a client-generated idempotency key. Replaying an op with a known `opId` returns
`accepted:true, duplicate:true` and does **not** advance the revision — that is what makes a
reconnect safe.

`oldValue` is mandatory except for `add_node` and the snapshot kinds (`snapshot_start`,
`snapshot_end`, and any op carrying the `snapshot` flag) — those state what the scene *is*, so there
is nothing to compare against. For every other op the server compares it with its authoritative
document and rejects a mismatch with `stale_revision` plus the current `revision`, so the client can
resync without another round trip.

Structural ops carry what the receiver needs to rebuild the same result: `parentNodeId` (empty for
a scene root), `siblingIndex`, `componentType` (assembly-qualified-ish type name) and `prefabGuid`
for a prefab instance. `add_node` sends an `AddNodePayload` JSON in `newValue`; `remove_node` sends
the removed node's payload in `oldValue`, so a late joiner can be told what was deleted.

Structural ops require the sender to hold the `nodeId` lock (`locked`); any participant may
restructure, but only the host may `SaveScene`. A **host snapshot** is exempt from locks — a guest
holding a lock must not leave a late joiner with a half-built scene — and the exemption is host-only,
so a guest cannot set `snapshot` to skip a lock (`locked`).

### op kinds

| kind | meaning | structural (needs object lock) |
|---|---|---|
| `add_node` | create an object/component | yes |
| `remove_node` | delete | yes |
| `reparent` | change parent | yes |
| `reorder_children` | change sibling order | yes |
| `add_component`, `remove_component` | add a component | yes |
| `set_property` | scalar/vector property | no (last-write-wins) |
| `set_prefab_override` | prefab instance modification | no |
| `snapshot_start` | first op of a host snapshot (`newValue` = node count) | no |
| `snapshot_chunk` | one chunk; `newValue` is a `SnapshotChunk` JSON (`ops[]`, `index`, `total`) | no |
| `snapshot_end` | last op of a host snapshot (`newValue` = node count) | no |
| `snapshot_request` | guest asks the host to resend (`newValue` = nodes it has) | no |

### snapshots

A participant who joins after edits began has an empty scene: the server keeps the op log, not the
scene. The host therefore streams its scene on demand.

```
host  → op { opKind: "snapshot_start",   newValue: "<nodes>", snapshot: true }
host  → op { opKind: "snapshot_chunk",   newValue: "<SnapshotChunk JSON>", snapshot: true }
host  → op { opKind: "snapshot_chunk",   ... }                       // as many as needed
host  → op { opKind: "snapshot_end",     newValue: "<nodes>", snapshot: true }
guest → op { opKind: "snapshot_request", newValue: "<nodes it has>" } // optional nudge
```

- Chunks are sent **one at a time**: the next chunk waits for the ack of the previous one.
- Parents always precede their children, so the receiver can build the hierarchy op by op.
- The receiver removes nodes it holds that the snapshot did not mention, then records the snapshot's
  node ids. The result is the host's scene, not a union of both.
- Local diffing is suppressed until `snapshot_end`, so applying a snapshot does not echo back as new
  ops.
- A snapshot in flight does not block the room: other participants' ops still apply on the host, and
  they simply arrive after the snapshot.

### value encoding

Property values are plain strings, because the same op has to survive a JSON round-trip through
both `JsonUtility` (Unity) and `System.Text.Json` (server). Everything is culture-invariant: `1.5`,
never `1,5`, and floats use round-trip formatting so a value is exactly reversible.

| type | wire form |
|---|---|
| `float` / `int` / `bool` / `string` / `enum` | the value itself; enums travel as their index |
| `Vector2` / `Vector3` / `Vector4` / `Color` / `Quaternion` | comma-separated components (`x,y,z`) |
| `Vector2Int` / `Vector3Int` / `RectInt` / `BoundsInt` | comma-separated integers |
| `Rect` | `x,y,width,height` |
| `Bounds` | `centerX,centerY,centerZ,extentX,extentY,extentZ` — extents, not size |
| `LayerMask` | the mask as an integer |
| arrays of the above (≤ 64 elements) | `count;element;element…` — `2;0,0,0;5,5,5` |

A string inside an array escapes `%` → `%25` and `;` → `%3B`, so a separator can never be mistaken
for the next element. Writing an array sets its size first, so a change in element count syncs too.
Arrays larger than 64 elements and empty arrays are not tracked: there is nothing to compare, and
one terrain layer would otherwise be a megabyte per keystroke.

A value the codec cannot express is refused (`TryWrite` returns false) rather than approximated —
the applier reports the rejection instead of writing a rounded number that quietly differs from the
sender's scene.

### errors

| code | when |
|---|---|
| `bad_token` | missing/wrong token (HTTP 401 before upgrade) |
| `protocol_mismatch` | version outside `[MinCompatible, Current]` |
| `not_initialized` | a non-`hello` message before `hello` |
| `malformed` | unreadable JSON, missing `type`, unknown `type` |
| `stale_revision` | `oldValue` (or `revision`) disagrees with the server |
| `room_full` | reserved |
| `scene_mismatch` | the room's scene GUID differs from the joiner's |
| `locked` | structural op on a node another participant holds |
| `not_host` | structural op from someone who is not the host |

## Locks

```
client → lock   { type, nodeIds[] }
client → unlock { type, nodeIds[] }
server → lock   { type, participants[], revision }   // the full set, not a delta
```

The server never applies deltas to a client's lock view: every response carries the **whole** set of
locks it holds, so a client that missed a frame converges on the next one instead of believing a
stale lock. Locks are advisory for values and mandatory for structural ops.

## Assets

```
host   → assets { type, assetGuids[] }      // GUID only, never paths or binaries
client → assets { type, assetGuids[] }
```

The host publishes the GUID list of the scene's dependencies. Each client compares it against its own
`AssetDatabase` and reports what it is missing. GUIDs are used instead of a count so a client can
name the missing asset — and so a client with an older version of the same assets cannot pass
silently.

## Presence

```
client → presence { type, selectedNodeIds[] }
server → presence { type, participants[], revision }
```

Selection is presence-only: it is how another editor draws "someone else is editing this".
It never changes the document.

## Scene-view cameras

```
client → view { type, sceneGuid, viewPosition, viewRotation, viewPivot, viewSize, viewFov }
server → view (to every other participant in the room)
```

A camera pose is presence, not an edit: it never carries a `revision`, never enters the op log and is
never replayed, so a peer camera cannot advance or corrupt the document. Clients publish at ~10 Hz and
only when the pose actually changed; a receiver drops a peer camera after 5 s of silence. Poses are
drawn as an editor overlay (`HideFlags.DontSave`) — they are not scene objects and are never saved.

## Room listing

`GET /rooms` returns what a client needs to pick a session, and nothing more:

```json
{ "rooms": [ { "room": "oa-main", "sceneName": "SampleScene",
               "sceneGuid": "629ae383...", "hostName": "Obsihill",
               "participants": 2, "revision": 41 } ] }
```

The token is required (401 otherwise). Asset GUID lists are **not** listed here — they are delivered
over the socket by the host, to participants only.

## Versioning

`ProtocolVersion.Current` / `ProtocolVersion.MinCompatible`. Additive fields do not bump the
version (both sides ignore unknown fields). Removing or renaming a field does, because those
names are the protocol.
