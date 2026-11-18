using SimConnector;

namespace SimConnector.Tests;

/// <summary>
/// SimVar name parsing: "NAME[unit]|alias", the fallback unit/value rules, and the
/// CLIENT: routing in <see cref="SimVarParser.CreateReference"/>.
/// </summary>
public class SimVarParserTests
{
    [Theory]
    [InlineData("PLANE ALTITUDE", "PLANE ALTITUDE", null, null)]
    [InlineData("PLANE ALTITUDE|ALT", "PLANE ALTITUDE", null, "ALT")]
    [InlineData("PLANE ALTITUDE[feet]", "PLANE ALTITUDE", "feet", null)]
    [InlineData("PLANE ALTITUDE[feet]|ALT", "PLANE ALTITUDE", "feet", "ALT")]
    [InlineData("  PLANE ALTITUDE [feet] | ALT  ", "PLANE ALTITUDE", "feet", "ALT")]
    [InlineData("PLANE ALTITUDE|", "PLANE ALTITUDE", null, null)]
    [InlineData("A|B|C", "A", null, "B|C")]
    [InlineData("", "", null, null)]
    [InlineData("   ", "", null, null)]
    public void ParseVarName_splits_unit_and_alias(string input, string expectedName, string? expectedUnit, string? expectedAlias)
    {
        var (varName, unit, alias) = SimVarParser.ParseVarName(input);

        Assert.Equal(expectedName, varName);
        Assert.Equal(expectedUnit, unit);
        Assert.Equal(expectedAlias, alias);
    }

    [Fact]
    public void ParseVarName_with_empty_brackets_gives_an_empty_unit_not_null()
    {
        // "VAR[]" counts as an explicit unit, so it suppresses the caller's fallback unit.
        var (varName, unit, alias) = SimVarParser.ParseVarName("PLANE ALTITUDE[]");

        Assert.Equal("PLANE ALTITUDE", varName);
        Assert.Equal(string.Empty, unit);
        Assert.Null(alias);
    }

    [Theory]
    [InlineData("CLIENT:PMDG_NG3_Data:0x1B5:bool", true)]
    [InlineData("client:pmdg_ng3_data:0x1b5", true)]
    [InlineData("  CLIENT:X:1", true)]
    [InlineData("CLIENTS:X:1", false)]
    [InlineData("PLANE ALTITUDE", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsClientDataName_matches_only_the_client_prefix(string? input, bool expected)
    {
        Assert.Equal(expected, SimVarParser.IsClientDataName(input));
    }

    [Fact]
    public void CreateReference_prefers_the_unit_and_alias_embedded_in_the_name()
    {
        var reference = SimVarParser.CreateReference("PLANE ALTITUDE[feet]|ALT", unit: "knots", value: 5.5);

        Assert.Equal("PLANE ALTITUDE", reference.SimVarName);
        Assert.Equal("feet", reference.Unit);
        Assert.Equal(5.5, reference.Value);
        Assert.Equal("ALT", reference.OutputAlias);
        Assert.Equal("ALT", reference.GetOutputName());
        Assert.False(reference.IsClientData);
    }

    [Fact]
    public void CreateReference_falls_back_to_the_given_unit_and_value()
    {
        var reference = SimVarParser.CreateReference("PLANE ALTITUDE", unit: "knots", value: 120);

        Assert.Equal("PLANE ALTITUDE", reference.SimVarName);
        Assert.Equal("knots", reference.Unit);
        Assert.Equal(120, reference.Value);
        Assert.Null(reference.OutputAlias);
        Assert.Equal("PLANE ALTITUDE", reference.GetOutputName());
    }
}
