# Security Policy

## Reporting

Open a private security advisory on GitHub, or email the maintainer listed in `package.json`.
Please do not open a public issue for anything that lets a client read or modify another
team's scene.

## Threat model (read this before exposing the server)

The server holds **your full scene content** (node graph, property values). It is not a
cache. Treat it like a database:

- **Token is required.** The server refuses to start without `SCENECOLLAB_TOKEN` unless
  you explicitly pass `--insecure-dev`. There is no anonymous mode in a release build.
- **Bind is loopback by default.** Set `SCENECOLLAB_BIND=0.0.0.0` only when a reverse
  proxy in front of it terminates TLS.
- **Always put TLS in front.** `ws://` over the open internet leaks scene contents and the
  token. Use `wss://` via your proxy.
- **No scene data is written to disk by default.** Snapshots are opt-in (`SCENECOLLAB_SNAPSHOT_DIR`).
- **Bearer tokens are compared in constant time**; failures return `401` with no detail.

## Out of scope

- A malicious **scribe** can save anything to the scene file. The scribe is a human role,
  not a sandbox.
- Clients are trusted to send syntactically valid operations. The server validates
  revision and identity, not scene semantics.
