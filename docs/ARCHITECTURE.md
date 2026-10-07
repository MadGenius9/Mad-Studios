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
  MadModStudio.AI            multi-provider layer (Anthropic SDK, OpenAI, Gemini, xAI, custom OpenAI-compatible),
                             model catalog + profiles, Auto Model Router, performance tracker, budget guard,
                             project knowledge + context builder, 11 specialist agents, coordinator (task graph,
                             file ownership, approvals, escalation, second opinions), secret storage (DPAPI)
  MadModStudio.App           WPF desktop app (MVVM, CommunityToolkit.Mvvm, AvalonEdit, DI)
  MadModStudio.Cli           `mms` headless front-end over the same services (scripting, CI, verification)
.github/workflows/ci.yml     build + test on Windows and Linux, publish the desktop app artifact
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
| `config/model-profiles.json` | Editable model capability/pricing rules used by the router (created from defaults). |
| `madmodstudio.db` → `ai_spend` | Ledger of every AI request (tokens, estimated cost or unpriced). |
| `config/models-cache.json` | Last successful model discovery per provider (no secrets). |
| `logs/` | Application logs (secrets redacted). |
| `deployments/<project>/<timestamp>/backup/` | Copies of game mod folders replaced by Deploy to Game (used by Undo). |

The live game `Mods` folder is only ever written by **Deploy to Game**, an explicit user action (see below). Game
assemblies are read fully into memory (Roslyn references, decompiler) so no handle or memory map keeps the game's
`Managed` folder locked — a regression test checks this.

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

## Deploy to Game (`Game7DTD/Deploy`)

`ModDeployService` installs the newest clean package (compiled, validated, packaged without errors) into the game
profile's Mods folder. The package is extracted with `SafeZip` and must contain exactly one mod folder with a valid
ModInfo.xml; the folder name is checked so nothing can be written outside Mods. An existing folder is copied to a
backup first, and a SHA-256 manifest of the deployed files is recorded (`deployments` table). Deploying refuses while
a `7DaysToDie*` process runs. Undo removes the deployed folder and restores the backup; it refuses when deployed files
were edited afterwards (unless forced) or when a newer deployment of the same folder is still active. Duplicate mod
names elsewhere in Mods produce a warning.

## Mod conflicts (`Game7DTD/Scanner/ModConflictAnalyzer`)

Read-only, deterministic analysis across all mods of a Mods folder, reported in folder-name (load) order:
XML `set`/`setattribute`/`removeattribute` on the same game value (Warning, or Info when the values agree), `remove` of a
node another mod patches, two mods appending a definition with the same element and `name`, Harmony patches on the
same method (Warning when a prefix or transpiler is involved, Info for postfixes only), one assembly name shipped by
several mods (Warning when versions differ), duplicate ModInfo Names (Error) and localization keys with different
text. XPaths are evaluated against the profile's game Config XML and compared by the selected node, so different
spellings of the same target match; without game XML only identical XPaths are compared. The Batch Scanner shows a
Conflicts tab and per-mod counts, `mms scan` prints them, and Deploy to Game adds XML/localization conflicts that
involve the deployed mod to its warnings.

## Safe fixes (`Game7DTD/Repair/SafeFixService`)

Mechanical fixes with exactly one correct outcome: `config` → `Config` folder case, legacy `<ModInfo>` wrapper →
current layout, missing ModInfo Name (from the folder name) or Version (placeholder 1.0.0), and creating a missing
ModInfo.xml for a folder with content. Anything needing judgement (wrong folder hierarchy, malformed XML, bundled
Harmony, renaming mod identifiers) is reported only. `Plan` is read-only; fixes are applied to an imported Repair
project between "Before safe fixes" / "Safe fixes applied" revisions and verified by the real build pipeline. A
folder that has not changed since it was fixed reuses its earlier project. Clean results can be installed with
Deploy to Game.

## AI layer (multi-model, multi-agent)

### Providers and models

* `IAIProvider` — provider-neutral contract (`TestConnectionAsync`, `ListModelsAsync`, `RunAsync` with a tool loop the
  provider owns so native formats round-trip). Implementations: `AnthropicProvider` (official Anthropic C# SDK),
  `OpenAICompatibleProvider` (OpenAI and xAI Chat Completions), `GeminiProvider` (REST `generateContent`),
  `CustomEndpointProvider` (any OpenAI-compatible URL: local runtimes, gateways). `ConnectionState` is
  NOT CONFIGURED / Unverified / CONNECTED / FAILED; CONNECTED is only set by a real successful API call.
* `IModelCatalog` — models come from each provider's model-list API and are merged with `config/model-profiles.json`
  (tier, context, tool support, prices; regex rules, editable). The UI never contains hard-coded model names. Unknown
  prices stay unknown ("cost unknown"), never guessed. The last discovery is cached for offline start.
* Secrets — `DpapiSecretStore` (Windows, current user) with read-only environment fallbacks (`ANTHROPIC_API_KEY`,
  `OPENAI_API_KEY`, `GEMINI_API_KEY`, `XAI_API_KEY`). Keys are never shown, logged (`SecretRedactor`), stored in
  projects, exports, packages or Git.

### Routing, learning and cost

* `AIPolicy` — global defaults: default model, routing mode (AUTO / BEST QUALITY / BALANCED / FAST / ECONOMY /
  MANUAL), agent control (AUTOMATIC / GUIDED (default) / MANUAL), privacy (cross-provider routing off by default,
  source/log sharing, ask before each run), limits (3 concurrent agents, 3 repair attempts, 3 models per task),
  escalation (auto off, threshold 2), budgets, per-task model preferences. Projects can override routing, control
  and per-agent models (AI TEAM / MANAGE TEAM).
* `IModelRouter` — selection order: explicit model → task-type preference → MANUAL default → hard filters (tools,
  context size, availability, privacy: no cross-provider switch unless allowed) → weighted score of tier, speed,
  cost and **your own history**. Every decision carries human-readable reasons ("Why this model?").
* `IModelPerformanceTracker` — aggregates real task records (success = change compiled + validated + not restored
  away). Fewer than 3 samples → "INSUFFICIENT DATA"; no benchmark numbers are ever shown or used.
* `BudgetGuard` — every AI request is written to the `ai_spend` ledger (provider-reported tokens, cost from profile
  prices, or null = unpriced). Project budgets are checked against the ledger, so they survive restarts; session
  budgets count the current run; unpriced calls are reported, never estimated. The AI Models page shows spend by
  period, model and project and edits per-model prices (`ModelProfileStore.SetPrice` copies the matching rule into an
  exact-match rule so tier/context are kept); `mms ai spend` / `mms ai price` do the same from the CLI.

### Knowledge and context

* `ProjectKnowledgeService` — provider-independent project memory in SQLite: findings, verified API facts (checked
  against the local game index, positive and negative), failed attempts ("DO NOT REPEAT", e.g. members reported
  missing by CS1061), success patterns (reused only for the same game profile), build/validation evidence.
  `IGameUpdateListener`: when the game assemblies' fingerprint changes, API knowledge is marked STALE and re-verified.
* `IAIContextBuilder` — builds only the sections a given agent needs (project, task, game profile, do-not-repeat,
  verified/stale API findings, latest build/validation, log findings, other agents' findings, recent changes) and a
  structured handoff, so any model can continue a task without the previous provider's chat history.

### Agents and coordination

* `AgentCatalog` — Lead, Architect, Game API Research, C#/Harmony, XML/XPath, Log Detective, Compiler Repair,
  DLL Analysis, Compatibility, Validator (independent reviewer) and Documentation. Each has its own tool set and
  write permission; evidence from local tools outranks AI interpretation.
* `AgentToolbox` — local tools (index search, file read/list, diagnostics, logs, version diff, findings, task plan,
  `propose_file_changes`). Agents must read a file before proposing changes to it; proposals never write directly.
* `AgentCoordinator` — gathers evidence (analysis, build, diagnosis) → Lead submits a validated task graph → tasks run
  in parallel up to the concurrency limit → each proposal is approved (GUIDED/MANUAL) and applied through
  `AgentChangeService` with a revision first (metadata: agent, provider, model, task) under single-writer file
  ownership with conflict detection → compile/repair loop → validators → independent review on a different model →
  package. Repeated failures produce an escalation suggestion (TRY x / KEEP CURRENT / CHOOSE MODEL) or, if enabled,
  an automatic switch. "Ask another model" evaluates an alternative proposal in a sandbox build; the compiler and
  validators decide, not AI votes.
* Live progress — providers bracket every model call with `RequestStarted` / `ResponseReceived` events (with that
  turn's real token counts); the coordinator turns them and tool calls into each task's `CurrentActivity`, which the
  Agent Board shows with elapsed time ("Waiting for model (turn 2) · 34s · working 2m 10s") and the CLI prints. A task
  waiting in the Approvals queue says so. No progress is simulated: between events only the clock advances.
* `IAIConsentService` — before a run, the user sees which provider(s) receive which data categories.

## Desktop app

WPF + MVVM (CommunityToolkit.Mvvm) + Microsoft.Extensions.DependencyInjection. Left navigation (Home, New Mod,
Repair Mod, My Mods, Batch Scanner, AI Models, Game Profiles, Settings) and a global default-model selector in the
status bar. The project screen has files on the left, tabs in the center (Overview, Files, Agents, Analysis, Build,
Validation, History, Logs). The Agents tab holds the Agent Board (IDLE / PLANNING / WORKING / WAITING / BLOCKED /
NEEDS REVIEW / FAILED / COMPLETE with details: actions, files, findings, routing reasons — never hidden reasoning),
approvals with diffs, escalation, AI TEAM, proposals (second opinion, restore before this agent change), report,
activity and handoff, the build pipeline always visible on the right and
diagnostics at the bottom. The editor is AvalonEdit with C#/XML/JSON highlighting; decompiled code is shown read-only
under an explicit "DECOMPILED / RECONSTRUCTED SOURCE" banner. Unhandled UI exceptions are logged and reported
without terminating the app.
