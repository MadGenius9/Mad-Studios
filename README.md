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
* AI (optional, bring your own Anthropic API key): repair loop and natural-language mod builder that must look up
  real game APIs before using them.

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the design.

## Requirements

* Windows 10/11 for the desktop app
* [.NET 10 SDK](https://dotnet.microsoft.com/download) to build
* A local 7 Days to Die installation (game or dedicated server)
* Optional: an Anthropic API key for AI features

## Build and run

```powershell
dotnet build MadModStudio.sln -c Release
dotnet run --project src/MadModStudio.App -c Release      # desktop app
dotnet test MadModStudio.sln                               # 100+ tests
```

The CLI (`mms`) uses the same engine and data folder:

```powershell
dotnet run --project src/MadModStudio.Cli -- profile add "C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die"
dotnet run --project src/MadModStudio.Cli -- search EntityDrone
dotnet run --project src/MadModStudio.Cli -- import C:\Mods\MyMod_1.0.8.zip
dotnet run --project src/MadModStudio.Cli -- build <projectId> --version 1.0.9
dotnet run --project src/MadModStudio.Cli -- diagnose <projectId> --log output_log_client.txt --working C:\Mods\MyMod_1.0.7.zip
dotnet run --project src/MadModStudio.Cli -- scan "C:\...\7 Days To Die\Mods"
```

## First proof on your machine

1. Launch the app → **Game Profiles → Add 7 Days to Die Installation** → select your game folder. Indexing runs
   automatically; check the detected version, paths and index counts. Try searching `EntityDrone`.
2. **My Mods → Import Mod ZIP** → pick a mod that includes C# source.
3. Open the project: **Analysis** shows type, XML/DLL counts, Harmony patches; **DLL Inspector** shows metadata.
4. **Files** → edit a `.cs` file → Ctrl+S (a revision is recorded).
5. **Build** → set the version (+1) → **Build Package**. The pipeline panel shows each real stage; the package path
   is shown with an *Open Folder* button. The original ZIP is untouched; **History** lists every step.

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
server-side/EAC assessments, AI provider + repair loop + mod builder, WPF app and CLI.

Coming soon (disabled in the UI): Deploy to Game, batch "Repair All Safe Fixes", OpenAI/Gemini providers, GitHub sync.
