# Design and limits

Why the obvious approach (sync the `.unity` file) cannot work, and what replaced it.

## 1. A Unity scene file is not a document you can merge

- `EditorSceneManager.SaveScene` rewrites the **whole** YAML file.
- Object anchors (`--- !u!1 &12345`) are **file-local** and get reassigned on save. The same
  GameObject has different anchors in two editors.
- Prefab instances exist in the scene only as a `PrefabInstance(1001)` block plus a
  `m_Modifications` array; there is no object to diff.
- A 160k-line scene is normal in production. Merge tools do not scale to that; they run out of
  context before they run out of patience.

So: **do not share the file. Share the edits.** The file stays a build artifact of a save.

## 2. Server-authoritative operations

```
Editor A ──op──▶ server (authoritative document) ──ack──▶ A
                        └───────────op──────────▶ Editor B, C…
```

- The server owns a revision counter and applies every op **exactly once**.
- Every op carries `oldValue`. A mismatch = someone else changed it first = reject with the
  current revision, so the client resyncs instead of silently overwriting.
- Structural edits (add/remove/reparent/reorder/component) require the **object lock**. Scalar
  edits are last-write-wins, because locking every float would make the tool unusable.
  CRDTs were considered and rejected: they need conflict-free semantics per property type, and
  a scene has thousands of them (`m_Modifications`, `m_LocalEulerAnglesHint`, …).

## 3. One writer

Only the **scribe** — the first participant in the room — calls `SaveScene` and commits to Git.
Everyone else's `Ctrl+S` is intercepted. This is not a limitation to remove later; it is the
reason two people can work in one scene and still produce one clean commit.

## 3-1. One scene, one host (v0.3)

"Everyone edits the same file" is a lie the protocol cannot support, so v0.3 makes the session
explicit instead:

- A room is bound to **one scene GUID**. A client with a different scene open is refused at `hello`
  (`scene_mismatch`) rather than being allowed to edit a document it cannot see. Before this, a
  second editor with a different scene silently received ops for objects it did not have.
- The room's **first participant is the host**, and the host is the scribe. Host and scribe are no
  longer independent: two roles that could disagree produced a state where nobody was allowed to
  save.
- The scene's identity lives in the room, not in a config file, so joining is "pick the scene someone
  has open" — `GET /rooms`, then connect. The client does not need to be told a room name.
- Host departure **promotes the longest-connected participant**, not the oldest client id. The
  surviving session continues with the same scene and revision; there is no re-host step.

Structural ops stayed in this release because they were the one thing that made "several people
editing a scene" feel false: you could move a cube together but not add one.

## 3-2. Components are watched by signature (v0.4)

A component added or deleted in the inspector fires neither a property modification nor a hierarchy
change. `SceneStructure` therefore keeps a per-object signature — the sorted list of component type
names — and compares it on every structural sweep. The signature is a string and keeps duplicates,
because Unity allows two colliders on one object and a set would report the second one as no change.

Deleting a remote component goes through `DestroyImmediate`, not `Destroy`: in a batch-mode editor
there is no frame after this one, so a deferred destroy never happens. Deleting a component that is
already gone counts as applied — a replay after a reconnect is normal, not an error.

Writing a property on a prefab instance is not enough to make it an override. Unity records
`m_Modifications` only along the paths it watches, so a value written straight through
`SerializedProperty` can sit on the instance and be dropped by the next save. The receiving editor
therefore calls `PrefabUtility.RecordPrefabInstancePropertyModifications` after the write. Two editors
can still record the array in different orders; normalising that order is deliberately left out, since
Unity rewrites the array on save anyway.

## 4. Stable ids

`GlobalObjectId` (scene GUID + fileID + prefab context) is hashed with FNV-1a into a 16-char
node id. `Foo.unity.ids.json` is the sidecar repair table for renames and for the rare hash
collision; it is committed. A hidden in-scene `__CollabIds` object was rejected: it ships into
builds and breaks on Missing Script.

## 5. What this does not do

| Not supported | Reason |
|---|---|
| Simultaneous editing of one file on disk | §1 |
| Live prefab **asset** editing | assets are not scene objects; out of scope |
| Prefab instance field overrides (`m_Modifications`) | normalising one instance's diff into a stable override array is v0.4; creating and deleting instances works |
| Component removal | v0.4 |
| `TerrainData` heightmap/splatmap | v0.5: stroke-sized region deltas on a dedicated channel, host-authoritative. 16-bit height tiles (128²), byte splat tiles (64², shrinking with layer count) |
| `TerrainData` trees/details/holes | a 1024² layer is megabytes and holes are sparse; not on the channel yet |
| Object/asset reference fields, arrays, `AnimationCurve`, `Gradient` | the value codec covers scalars, vectors, colour, enum, string only |
| Undo across editors | each editor's undo stack is local; undoing someone else's edit is not modelled |
| 100k+ line monolithic scenes | op latency + resync cost; split scenes additively |
| Offline editing | the server is authoritative; no local op queue yet |
| Non-Unity clients | the op vocabulary is Unity's serialisation model |

## 6. Security posture

- No token → server refuses to start. Loopback by default.
- Token comparison is constant-time; the header is preferred over the query string (query
  strings end up in proxy access logs).
- The Unity client keeps the token in `EditorPrefs`, never in `ProjectSettings/`, because
  `ProjectSettings/` is committed.
- The server never executes anything from a client: ops are data, applied by the editor that
  receives them.

## 8. What a big scene costs (v0.5.5)

The obvious worry about a tool like this is that it walks the scene every tick and falls over on a
large level. It does not walk the scene every tick — the change feed hands over only the objects that
Unity says were touched — but "it should be fine" is not a number, so the suite now measures the real
diff, registry and structure walk over 1500 objects. These are this machine's figures (Ryzen AI 9
HX 370, Unity 6000.6.4f1, EditMode, batch), logged as `[BENCH]` on every test run:

| Operation | Scene size | Cost |
|---|---|---|
| Baseline one object (first join / snapshot) | 1500 objects | 82 ms total — once, not per tick |
| Steady-state diff sweep, nothing changed | 1500 objects | 90 ms for a *full* sweep, 0 ops |
| Diff of a tenth of the scene moved | 1500 objects | 89 ms, 150 ops |
| One object, all tracked fields | — | ~0.15 ms |
| Structure walk (`DetectUnregistered`) | 1500 objects | 1.0 ms |
| Terrain height poll, 1024² heightmap | 81 wire tiles | 21–25 ms, no 4 MiB array |

Two numbers matter. The per-tick cost is proportional to **what was edited**, not to scene size: the
full sweep above only happens on the first sync. And the terrain poll now reads one 128×128 tile at a
time, so a poll allocates 16 KiB per tile instead of a 4 MiB array plus a full copy — the allocation
that would otherwise surface as a hitch an hour into an editing session.

The ceilings in the benchmark are deliberately loose (seconds, not milliseconds). They are there to
catch an order-of-magnitude regression, not to police a loaded CI box.

## 7. Known risks

| Risk | Mitigation |
|---|---|
| Package ↔ server protocol skew | `ProtocolVersion` range check at `hello`; both released from one tag |
| Prefab override normalisation (`m_Modifications` ordering) | W1 acceptance test: apply → save → normalised YAML diff must be zero; if it fails, v0.1 restricts prefab instances to transform/name |
| Large scenes | measure before promising; scene splitting guidance in README |
| A client missing assets | host publishes the dependency GUID list, the client reports the gap and substitutes a placeholder; a missing mesh would otherwise render as **nothing at all** in Unity |
| Host leaves mid-edit | longest-connected promotion, `welcome` re-sent to everyone; ops in flight are replayed idempotently by `opId` |
