# Contributing

## Ground rules

1. **The wire protocol has exactly one copy of its source** —
   `unity/com.obsihill.scenecollab/Contracts/`. The `contracts` .NET project links those files.
   Never add a copy under `server/` or `contracts/`; CI rejects it.
2. **`LangVersion` stays 9.0** (`Directory.Build.props`). Unity 6000 compiles the shared files as
   C# 9, so C# 12 syntax in a shared file breaks the editor and not the server — the worst kind
   of failure to debug.
3. **`WireMessage` stays flat**: public fields, no inheritance, no generics, no dictionaries.
   It has to survive `JsonUtility` and untrusted input.
4. **Never commit a token, an absolute `file:` package path, or `UnityYAMLMerge.exe`.**
   The first leaks access, the second breaks every other machine's build, the third is a Unity
   redistribution we have no right to publish.
5. **No game project content in this repo.** Samples are cubes and lights only.

## Before you open a PR

```bash
dotnet build SceneCollab.slnx
dotnet test  SceneCollab.slnx
```

For anything that touches the Unity package, compile it in a real project (an empty Unity 6000
project with the package added and listed in `testables` is enough) and run the EditMode tests.
Unity compile errors do not show up in `dotnet build`, because the package sources are compiled
by two different toolchains on purpose.

## Architecture boundaries

```
Editor ──▶ Domain ──▶ Contracts
   └────────▶ Transport (Editor)
```

- `Contracts` — wire types only, `noEngineReferences`, referenced by both sides.
- `Domain` — pure C#, no `UnityEngine`, so it is testable without a scene.
- `Editor` — the only assembly allowed to touch Unity APIs; `includePlatforms: ["Editor"]`.
- Server `Domain` is a *different* assembly with the same name; only `Contracts` is shared.

Do not add a reference from `Domain` to `Editor`, and do not let `Contracts` depend on anything.
