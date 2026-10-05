# Mad Mod Studio — Architecture

Mad Mod Studio is a Windows desktop development environment for 7 Days to Die mods: import, analyze, edit,
compile, validate, version, package, repair, and (with an AI provider) generate mods — always grounded in the
user's **installed** copy of the game.

## Principles

1. **The installed game is the source of truth.** Types, members, XML paths, localization keys and the target
   runtime are discovered from the user's install (metadata + config files). Nothing about a specific 7DTD release
   is hard-coded; the AI must search the local index before using any API.
2. **Imported mods are untrusted.** DLLs are read with `System.Reflection.Metadata`/ICSharpCode.Decompiler as
   data — never loaded into an `AssemblyLoadContext` or executed. ZIPs are extracted with path-traversal,
   device-name and zip-bomb protection.
3. **No fake functionality.** Every pipeline stage reports what actually happened; a stage is "Succeeded" only when
   the underlying operation succeeded. Unimplemented features are disabled and labeled "Coming Soon".
4. **Source safety.** Originals are preserved read-only. Every change (manual edit, version bump, AI patch,
   restore) goes through a revision in a Git-format history.
5. **No redistribution.** Game assemblies are referenced in place; only paths/metadata are stored.

## Solution layout

```
MadModStudio.sln
src/
  MadModStudio.Core          domain models, abstractions, safe ZIP/IO, Git-format revision store, validation framework
  MadModStudio.Persistence   SQLite application database (profiles, projects, revisions, builds, settings)
  MadModStudio.ModAnalysis   generic .NET analysis: metadata reader, Harmony detection (IL + source), decompiler, diffs
  MadModStudio.Compiler      Roslyn compiler service, csproj hint reader, structured diagnostics
  MadModStudio.Packaging     ZIP packager + package inspector (exclusion rules, single root folder)
  MadModStudio.Game7DTD      the 7 Days to Die module: install detection, version/runtime detection, knowledge index,
                             ModInfo, importer, analyzer, validators, log parser, repair diagnosis, batch scanner,
                             build pipeline, project service
  MadModStudio.AI            provider abstraction, Anthropic provider (official SDK), local context tools,
                             repair loop, natural-language mod builder, secret storage (DPAPI)
  MadModStudio.App           WPF desktop app (MVVM, CommunityToolkit.Mvvm, AvalonEdit, DI)
  MadModStudio.Cli           `mms` headless front-end over the same services (scripting, CI, verification)
tests/
  MadModStudio.Core.Tests / Game7DTD.Tests / Compiler.Tests / Packaging.Tests
  Shared/FakeGame.cs         builds a synthetic install (stub Assembly-CSharp + Harmony compiled with Roslyn)
  FakeGameGenerator          dev tool: writes that synthetic install + sample/broken mods to disk
```

Dependency direction (no cycles):

```
Core ← Persistence, ModAnalysis, Compiler, Packaging
Core, ModAnalysis, Compiler, Packaging ← Game7DTD
Game7DTD ← AI
everything ← App, Cli
```

Generic infrastructure (Core, ModAnalysis, Compiler, Packaging, Persistence) knows nothing about 7DTD. A second
game would be a new `MadModStudio.GameXyz` module providing install detection, an index builder, validators and a
reference resolver against the same interfaces (`IGameKnowledgeIndex`, `IModValidator`, `IModCompiler`, ...).

## Data locations

`%LOCALAPPDATA%\MadModStudio` (override: `MADMODSTUDIO_HOME`)

| Path | Contents |
|---|---|
| `madmodstudio.db` | SQLite metadata (WAL). Never contains assemblies. |
| `cache/game-index/<profile>/index.db` | Per-profile knowledge index (types, members, XML entries, localization). Rebuildable. |
| `workspace/projects/<id>/original/` | Imported ZIP/folder exactly as provided, read-only. |
| `workspace/projects/<id>/source/` | Editable working copy (mod folder + any source). |
| `workspace/projects/<id>/history.git/` | Bare Git object database of revisions (`git --git-dir=history.git log` works). |
| `workspace/projects/<id>/build/` | Compiler output and the staged mod folder. |
| `workspace/projects/<id>/output/` | Packaged ZIPs, e.g. `MadWorkingRacks_1.0.9.zip`. |
| `secrets/` | DPAPI-encrypted API keys (Windows, current user). |
| `logs/` | Application logs (secrets redacted). |

The live game `Mods` folder is never written to. "Deploy to Game" is planned as an explicit, user-initiated action.

## Game profile and knowledge index

`GameInstallLocator` accepts the install root or any folder inside it and discovers `*_Data/Managed/Assembly-CSharp.dll`,
the config folder (a folder containing `blocks.xml`/`items.xml`), `Mods/`, and `0Harmony.dll`.

`GameVersionDetector` decompiles only the `Constants` type and reads the `cVersionInformation` initializer; it falls
back to the Steam `appmanifest` build id. Every value records its source; unknown stays unknown.

`RuntimeProfileDetector` reads `mscorlib`/`netstandard` versions and `Assembly-CSharp`'s references. Mod DLLs are
compiled against **every managed assembly in the game's own Managed folder** (plus Harmony), so the target API
surface is exactly what the game ships — independent of the .NET runtime Mad Mod Studio runs on. Language version,
extra references and exclusions are editable per profile.

`GameIndexBuilder` writes a per-profile SQLite index: non-framework assemblies (private members included for
`Assembly-CSharp*`, since Harmony often targets private methods), named XML entries with XPath-style paths and line
numbers, and localization keys. `SqliteGameKnowledgeIndex` implements `IGameKnowledgeIndex` for search, hierarchy
member lookup and XML/localization queries.

## Build pipeline

`ModBuildPipeline`: **ANALYZE → GENERATE/REPAIR → COMPILE → VALIDATE → PACKAGE**

* Analyze: ModInfo, XML, DLLs (metadata), source, csproj hints, Harmony patches, server-side/EAC assessment.
* Generate/Repair: reported as Skipped unless an AI operation precedes the build (the AI engines drive the pipeline).
* Compile: `CompileInputResolver` decides units (per csproj or all `.cs`), assembly names and where each DLL belongs
  in the mod folder; `RoslynModCompiler` compiles in-process with metadata-only references and writes the DLL only on
  success. Diagnostics carry file/line/column.
* Validate: the mod folder is staged exactly as it will ship, then all `IModValidator`s run against it.
* Package: blocked by ERROR findings or a failed compile unless the user chooses **Package With Errors**, which
  produces a `_WITH-ERRORS` ZIP and never counts as a clean build. The ZIP is then re-validated for structure.

Each build stores a `BuildRecord` and stamps a revision with its build status.

## Validators (`Game7DTD/Validation`)

| Id | Checks |
|---|---|
| `modinfo` | presence, layout (legacy vs current), Name/Version/DisplayName |
| `structure` | Config folder casing, nested mods, loose XML, executables, empty mods |
| `xml-wellformed` | parse errors with line numbers |
| `xml-xpath` | evaluates every XPath patch against the installed game's XML; missing target files |
| `duplicates` | case-only duplicates, duplicate ModInfo/assemblies, bundled Harmony |
| `localization` | header, duplicate keys, added items/blocks without names |
| `dll-dependencies` | each referenced assembly resolvable from game/mod/Harmony/other installed mods; .NET 5+ targets |
| `game-api` | every type/method/field a DLL uses from the game exists in the installed version |
| `compiler` | compile outcome |
| `harmony-targets` | patch target types/methods/overloads exist (with "similar name" hints) |
| `server-side` | when the project is Server-Side Only, evidence that clients would need it |
| `package` | one top-level folder, ModInfo directly inside, no forbidden files |

Validators can declare themselves not applicable; skipped checks are never reported as passed. A validator that
throws becomes an ERROR finding instead of crashing the run.

## Repair mode

`RepairService.DiagnoseAsync`: analyze the mod, parse logs (`LogParser` streams huge files, classifies exceptions,
missing types/methods/fields, Harmony, XML, XPath, assembly loading, null references, groups by signature), correlate
groups with project files (file names, XPaths, Harmony patch classes/targets, stack-frame types), compare with the
last working version (`VersionComparer`: files, unified diffs, DLL metadata diffs), and check the mod against the
current game index. It produces "LIKELY REGRESSION" conclusions and proposed actions as text.

## AI layer

* `IAIProvider` — provider-neutral; the provider owns the tool loop so native message formats (e.g. signed thinking
  blocks) round-trip intact. `AnthropicProvider` uses the official Anthropic C# SDK (`claude-opus-5-5` by default,
  server-side refusal fallback enabled, typed error handling). OpenAI/Gemini can be added as further providers.
* `ModToolbox` — local tools the model calls to fetch only what it needs: game API/type/XML/localization search,
  project file listing/reading, diagnostics, logs, version diff, diagnosis. `propose_file_changes` records edits;
  it never writes files.
* `AIRepairEngine` — compile → structured diagnostics → AI (with game API lookups) → proposed patch → revision →
  apply → compile again, capped at 3 automatic attempts by default (configurable), then stops and shows diagnostics.
* `AIModBuilder` — classifies a plain-English request (XML-only / C#/Harmony / Hybrid / Needs client assets /
  Unknown), plans against the index, then generates files into a new project and runs the repair loop.
* `IAIConsentService` — before any AI operation, the user sees which data categories may be sent and must confirm.
* Secrets — `DpapiSecretStore` (Windows, current user) with `ANTHROPIC_API_KEY` as a read-only fallback. Keys are
  redacted from logs and never stored in projects.

## Desktop app

WPF + MVVM (CommunityToolkit.Mvvm) + Microsoft.Extensions.DependencyInjection. Left navigation (Home, New Mod,
Repair Mod, My Mods, Batch Scanner, Game Profiles, Settings). The project screen has files on the left, tabs in the
center (Overview, Files, AI, Analysis, Build, Validation, History), the build pipeline always visible on the right and
diagnostics at the bottom. The editor is AvalonEdit with C#/XML/JSON highlighting; decompiled code is shown read-only
under an explicit "DECOMPILED / RECONSTRUCTED SOURCE" banner. Unhandled UI exceptions are logged and reported
without terminating the app.
