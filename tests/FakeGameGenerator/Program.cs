using MadModStudio.TestSupport;

if (args.Length == 0)
{
    Console.WriteLine("Usage: FakeGameGenerator <outputFolder>");
    return 1;
}
var outDir = Path.GetFullPath(args[0]);
var game = Path.Combine(outDir, "steamapps", "common", "7 Days To Die");
FakeGame.Create(game);
var mods = Path.Combine(outDir, "mod-inbox");
Directory.CreateDirectory(mods);
var zip = SampleMod.WriteZip(mods);
var broken = SampleMod.WriteZip(Path.Combine(outDir, "broken"), "1.0.9",
    patchSource: SampleMod.PatchSource.Replace("\"depositInventory\"", "\"depositAllInventory\""),
    blocksPatch: SampleMod.BlocksPatch.Replace("cntStorageGeneric", "cntStorageGenericRemoved"));
Console.WriteLine($"Fake game:   {game}");
Console.WriteLine($"Sample mod:  {zip}");
Console.WriteLine($"Broken mod:  {broken}");
return 0;
