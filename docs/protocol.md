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
```

- Text frames, UTF-8, one JSON object per frame.
- Max inbound message: 4 MiB.
- Server pings with WebSocket keep-alive every 20 s.
- Room names: `^[A-Za-z0-9._-]{1,64}$`.

## Handshake

```
client → hello   { type, protocolVersion, displayName, room }
server → welcome { type, protocolVersion, clientId, displayName, color, room, revision, isScribe, participants[] }
server → presence (broadcast to the others; the joiner already has the roster in welcome)
```

The server rejects an incompatible `protocolVersion` **before** any room state exists, then
closes the socket with `policy_violation`. Failing early is the point: a client that speaks a
different version must not be able to half-join and corrupt a room.

## Operations

```
client → op { type, opId, clientId, revision, opKind, nodeId, propertyPath, oldValue, newValue }
server → ack { type, opId, accepted, duplicate, revision, code?, message? }
server → op  (broadcast to every other client in the room)
```

`opId` is a client-generated idempotency key. Replaying an op with a known `opId` returns
`accepted:true, duplicate:true` and does **not** advance the revision — that is what makes a
reconnect safe.

`oldValue` is mandatory except for `add_node`. The server compares it with its authoritative
document and rejects a mismatch with `stale_revision` plus the current `revision`, so the client
can resync without another round trip.

### op kinds

| kind | meaning | structural (needs object lock) |
|---|---|---|
| `add_node` | create an object/component | yes |
| `remove_node` | delete | yes |
| `reparent` | change parent | yes |
| `reorder_children` | change sibling order | yes |
| `add_component` | add a component | yes |
| `set_property` | scalar/vector property | no (last-write-wins) |
| `set_prefab_override` | prefab instance modification | no |

### errors

| code | when |
|---|---|
| `bad_token` | missing/wrong token (HTTP 401 before upgrade) |
| `protocol_mismatch` | version outside `[MinCompatible, Current]` |
| `not_initialized` | a non-`hello` message before `hello` |
| `malformed` | unreadable JSON, missing `type`, unknown `type` |
| `stale_revision` | `oldValue` (or `revision`) disagrees with the server |
| `room_full` | reserved |

## Presence

```
client → presence { type, selectedNodeIds[] }
server → presence { type, participants[], revision }
```

Selection is presence-only: it is how another editor draws "someone else is editing this".
It never changes the document.

## Versioning

`ProtocolVersion.Current` / `ProtocolVersion.MinCompatible`. Additive fields do not bump the
version (both sides ignore unknown fields). Removing or renaming a field does, because those
names are the protocol.
