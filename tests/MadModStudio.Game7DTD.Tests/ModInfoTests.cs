using MadModStudio.Game7DTD.Mods;
using MadModStudio.TestSupport;

namespace MadModStudio.Game7DTD.Tests;

public class ModInfoTests
{
    private static string Write(string xml)
    {
        var p = Path.Combine(FakeGame.TempDir("modinfo"), "ModInfo.xml");
        File.WriteAllText(p, xml);
        return p;
    }

    [Fact]
    public void Parses_current_format()
    {
        var info = ModInfoFile.Parse(Write(SampleMod.ModInfoXml));
        Assert.Equal(ModInfoFormat.V2, info.Format);
        Assert.Equal("MadWorkingRacks", info.Name);
        Assert.Equal("Mad Working Racks", info.DisplayName);
        Assert.Equal("1.0.8", info.Version);
        Assert.Equal("MadGenius", info.Author);
    }

    [Fact]
    public void Parses_legacy_format()
    {
        var info = ModInfoFile.Parse(Write("""<xml><ModInfo><Name value="Old Mod"/><Description value="d"/><Author value="a"/><Version value="2.0"/></ModInfo></xml>"""));
        Assert.Equal(ModInfoFormat.V1, info.Format);
        Assert.Equal("Old Mod", info.Name);
        Assert.Equal("2.0", info.Version);
    }

    [Fact]
    public void Malformed_xml_reports_line()
    {
        var p = Write("<xml>\n  <Name value=\"x\">\n</xml>");
        var info = ModInfoFile.TryParse(p, out var error);
        Assert.Null(info);
        Assert.Contains("malformed", error);
        Assert.Contains("line", error);
    }

    [Fact]
    public void SetVersion_preserves_other_fields_and_format()
    {
        var p = Write(SampleMod.ModInfoXml);
        ModInfoFile.SetVersion(p, "1.0.9");
        var info = ModInfoFile.Parse(p);
        Assert.Equal("1.0.9", info.Version);
        Assert.Equal("Mad Working Racks", info.DisplayName);
        Assert.Contains("<DisplayName value=\"Mad Working Racks\" />", File.ReadAllText(p));
    }

    [Fact]
    public void SetVersion_works_on_legacy_layout()
    {
        var p = Write("<xml><ModInfo><Name value=\"Old\"/><Version value=\"1.0\"/></ModInfo></xml>");
        ModInfoFile.SetVersion(p, "1.1");
        var info = ModInfoFile.Parse(p);
        Assert.Equal(ModInfoFormat.V1, info.Format);
        Assert.Equal("1.1", info.Version);
    }

    [Theory]
    [InlineData("1.0.8", "1.0.9")]
    [InlineData("2.9", "2.10")]
    [InlineData("1.0.0-beta", "1.1.0-beta")]
    [InlineData("", "1.0.0")]
    [InlineData("beta", null)]
    public void IncrementVersion(string input, string? expected) => Assert.Equal(expected, ModInfoFile.IncrementVersion(input));
}
