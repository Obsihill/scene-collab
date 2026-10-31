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

v0.2 moves **scene edits** between editors, verified both directions against a real server and a
real second participant. Structural edits (add/remove/reparent) and prefab overrides are next — see
the roadmap at the bottom of this file.

| Part | State |
|---|---|
| Server: `/health`, token auth, room isolation, presence | done |
| Operations: authoritative apply, broadcast, idempotent replay, conflict rejection | done (16 unit tests + live smoke test) |
| Unity package: install, window, connect, presence list | done (36 EditMode tests, compiled on Unity 6000.6) |
| Scene capture (`ObjectChangeEvents`) → `set_property` ops | done (position / rotation / scale / name) |
| Op apply (`SerializedProperty`), echo guard, scribe save interception, `*.unity.ids.json` | done |
| Structural ops, prefab `m_Modifications`, property ladders (components) | next |

Verified by `bash tools/live-run.sh`: it starts the server, a headless Unity editor and a Unity-free
peer, moves a cube from the peer into the editor, then from the editor back into the peer, and exits
non-zero unless both directions actually landed.

## 1. Install the Unity package

`Packages/manifest.json`:

```jsonc
"com.obsihill.scenecollab": "https://github.com/Obsihill/scene-collab.git?path=unity/com.obsihill.scenecollab#v0.2.2"
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

Window → server URL (`ws://localhost:8080/ws`) → access token → room name → **Connect**.
First client in a room becomes the **scribe**; the scribe saves the scene to disk.

---

## What this is not

| Not supported (v0.1) | Why |
|---|---|
| Editing one scene file simultaneously on disk | `SaveScene` rewrites the whole YAML and reassigns fileIDs — file-level merge cannot work |
| Prefab **asset** live editing | Assets are not scene objects; v0.1 covers scene instances only |
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

1. **v0.1** — connection layer (this release): protocol, server, package, tests.
2. **v0.2** — scene capture and apply for transform/name/active/enabled properties; scribe save.
3. **v0.3** — structural ops under object lock; prefab instance overrides; `*.ids.json` sidecar.
4. **v1.0** — first-run wizard (Lv1 host mode: server spawned locally, 6-digit code).

## License

MIT — see [`LICENSE`](LICENSE). `UnityYAMLMerge` is **not** bundled: it is Unity's redistribution
tool and lives in your Unity installation (`Editor/Data/Tools/`).
