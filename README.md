# Mad Mod Studio

An AI-assisted development, repair, validation, compilation, versioning and packaging environment for
**7 Days to Die** mods — grounded in *your* installed copy of the game.

* Add your 7DTD installation → it is validated and indexed (types/methods from `Assembly-CSharp.dll`, config XML,
  localization) without executing anything.
* Import a mod ZIP or folder → it is extracted safely into a workspace; the original is preserved.
* Inspect DLLs as metadata (types, members, Harmony patches and their targets, references), optionally decompiled.
* Edit C#/XML, compile DLLs **without Visual Studio** against the game's own assemblies, validate (XPath targets
  against the real game XML, game API references, Harmony targets, ModInfo, localization, structure…), bump the
  version and package an installable ZIP.
* Repair Mode: correlate client/server logs and a last-known-working version with the mod and the current game.
* Deploy to Game: install a clean package into the game's Mods folder on request — the previous folder is backed up
  and every deployment can be undone.
* Conflict detection: finds installed mods that patch the same game XML value, remove what another mod patches, add
  the same named block/item, Harmony-patch the same method, bundle different versions of one DLL, share a mod Name or
  disagree on localization text. XPaths are evaluated against your game's own XML, so differently written XPaths that
  hit the same node are still caught. Deploy to Game warns when the mod you install conflicts with one already there.
* Batch Scanner: scan a whole Mods folder read-only; "Repair All Safe Fixes" fixes imported copies (folder-name case,
  legacy ModInfo layout, missing ModInfo fields) with revisions, rebuilds them, and can deploy the clean results.
* AI (optional, bring your own keys): Anthropic, OpenAI, Google Gemini, xAI or any OpenAI-compatible endpoint. A team
  of 11 specialist agents plans, researches the installed game, implements, repairs and independently reviews — with
  dynamic model discovery, an Auto Model Router that learns from your real results, per-agent model choice,
  approvals (GUIDED by default), escalation suggestions, second opinions decided by compiler evidence, and a revision
  before every AI change.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design.

## Requirements

* Windows 10/11 for the desktop app
* [.NET 10 SDK](https://dotnet.microsoft.com/download) to build
* A local 7 Days to Die installation (game or dedicated server)
* Optional: an API key for at least one AI provider (or a local OpenAI-compatible server)

## Build and run

```powershell
dotnet build MadModStudio.sln -c Release
dotnet run --project src/MadModStudio.App -c Release      # desktop app
dotnet test MadModStudio.sln                               # 140+ tests
```

The CLI (`mms`) uses the same engine and data folder:

```powershell
dotnet run --project src/MadModStudio.Cli -- profile add "C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die"
dotnet run --project src/MadModStudio.Cli -- search EntityDrone
dotnet run --project src/MadModStudio.Cli -- import C:\Mods\MyMod_1.0.8.zip
dotnet run --project src/MadModStudio.Cli -- build <projectId> --version 1.0.9
dotnet run --project src/MadModStudio.Cli -- diagnose <projectId> --log output_log_client.txt --working C:\Mods\MyMod_1.0.7.zip
dotnet run --project src/MadModStudio.Cli -- scan "C:\...\7 Days To Die\Mods"
dotnet run --project src/MadModStudio.Cli -- safe-fix "C:\...\7 Days To Die\Mods" [--deploy]
dotnet run --project src/MadModStudio.Cli -- deploy <projectId>        # deploy list / deploy undo
dotnet run --project src/MadModStudio.Cli -- ai providers                 # status (keys: ai key <provider> <key>)
dotnet run --project src/MadModStudio.Cli -- ai models --refresh
dotnet run --project src/MadModStudio.Cli -- ai fix <projectId> "fix the build" --yes   # multi-agent repair
dotnet run --project src/MadModStudio.Cli -- ai performance               # real history only
```

## First proof on your machine

1. Launch the app → **Game Profiles → Add 7 Days to Die Installation** → select your game folder. Indexing runs
   automatically; check the detected version, paths and index counts. Try searching `EntityDrone`.
2. **My Mods → Import Mod ZIP** → pick a mod that includes C# source.
3. Open the project: **Analysis** shows type, XML/DLL counts, Harmony patches; **DLL Inspector** shows metadata.
4. **Files** → edit a `.cs` file → Ctrl+S (a revision is recorded).
5. **Build** → set the version (+1) → **Build Package**. The pipeline panel shows each real stage; the package path
   is shown with an *Open Folder* button. The original ZIP is untouched; **History** lists every step.

6. Optional AI: **Settings → AI Providers** → paste a key → **Save Key** → **Test Connection** (models are discovered
   automatically). Open the project's **Agents** tab, describe the task and click **Run Agents**. In GUIDED mode each
   change waits on the **Approvals** tab with its diff; **AI Models** shows performance from your own history.

No game files are copied or redistributed; mods are compiled against your installation's `Managed` folder.

## Development without the game

`tests/FakeGameGenerator` writes a synthetic install (stub `Assembly-CSharp`/Harmony compiled from source, sample
config XML) plus a sample mod and a deliberately broken version:

```bash
dotnet run --project tests/FakeGameGenerator -- ./sandbox
```

## Status

Implemented: game profiles + indexing, safe import, analysis, DLL inspector + decompiler view, compiler, validators,
packaging, revision history with restore, version comparison, log analyzer, Repair Mode diagnosis, batch scanner,
server-side/EAC assessments, deploy to game with undo, batch safe fixes, multi-provider AI with model discovery/routing/performance tracking, multi-agent
coordinator (task graph, approvals, escalation, second opinions, project knowledge, game-update awareness),
WPF app and CLI.

Deploy to Game (with backup/undo) and batch safe fixes are implemented. CI (`.github/workflows/ci.yml`) builds and
tests on Windows and Linux on every push and publishes the desktop app as a build artifact.

Coming soon (disabled in the UI): GitHub sync.
