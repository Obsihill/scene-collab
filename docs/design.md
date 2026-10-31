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

## 4. Stable ids

`GlobalObjectId` (scene GUID + fileID + prefab context) is hashed with FNV-1a into a 16-char
node id. `Foo.unity.ids.json` is the sidecar repair table for renames and for the rare hash
collision; it is committed. A hidden in-scene `__CollabIds` object was rejected: it ships into
builds and breaks on Missing Script.

## 5. What v0.1 does not do

| Not supported | Reason |
|---|---|
| Simultaneous editing of one file on disk | §1 |
| Live prefab **asset** editing | assets are not scene objects; out of scope for v0.1 |
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

## 7. Known risks

| Risk | Mitigation |
|---|---|
| Package ↔ server protocol skew | `ProtocolVersion` range check at `hello`; both released from one tag |
| Prefab override normalisation (`m_Modifications` ordering) | W1 acceptance test: apply → save → normalised YAML diff must be zero; if it fails, v0.1 restricts prefab instances to transform/name |
| Large scenes | measure before promising; scene splitting guidance in README |
