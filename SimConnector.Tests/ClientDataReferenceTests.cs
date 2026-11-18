using Microsoft.FlightSimulator.SimConnect;
using SimConnector;

namespace SimConnector.Tests;

/// <summary>
/// Parsing of the documented CLIENT: reference syntax
/// <c>CLIENT:AREA_NAME[@AREA_ID]:OFFSET[:TYPE]|OUTPUT_ALIAS</c> and the rules the read path
/// relies on: known areas resolve by name, everything else must publish an area ID, and a
/// malformed reference comes back with <c>ClientOffset = -1</c> so it is rejected instead of
/// being sent to SimConnect as a SimVar.
/// </summary>
public class ClientDataReferenceTests
{
    private const int PmdgNg3AreaId = 0x4E473331;

    [Fact]
    public void Parses_a_known_area_by_name()
    {
        var reference = SimVarParser.ParseClientReference("CLIENT:PMDG_NG3_Data:0x1B5");

        Assert.NotNull(reference);
        Assert.True(reference!.IsClientData);
        Assert.Equal("PMDG_NG3_Data", reference.SimVarName);
        Assert.Equal(PmdgNg3AreaId, reference.ClientAreaId);
        Assert.Equal(0x1B5, reference.ClientOffset);
        Assert.Equal(SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT64, reference.ClientSizeOrType);
        Assert.Equal("float64", reference.ClientTypeName);
    }

    [Fact]
    public void Area_lookup_is_case_insensitive()
    {
        var reference = SimVarParser.ParseClientReference("client:pmdg_ng3_data:0x1b5:bool");

        Assert.NotNull(reference);
        Assert.Equal(PmdgNg3AreaId, reference!.ClientAreaId);
        Assert.Equal(0x1B5, reference.ClientOffset);
    }

    [Theory]
    [InlineData("CLIENT:PMDG_NG3_Data:0x1B5", 0x1B5)]
    [InlineData("CLIENT:PMDG_NG3_Data:437", 437)]
    [InlineData("CLIENT:PMDG_NG3_Data:0x1a03", 0x1a03)]
    [InlineData("CLIENT:PMDG_NG3_Data: 0x1B5 ", 0x1B5)]
    public void Offset_accepts_hex_and_decimal(string input, int expectedOffset)
    {
        var reference = SimVarParser.ParseClientReference(input);

        Assert.NotNull(reference);
        Assert.Equal(expectedOffset, reference!.ClientOffset);
    }

    [Theory]
    [InlineData("CLIENT:MY_AREA@0x4E473331:16", PmdgNg3AreaId)]
    [InlineData("CLIENT:MY_AREA@437:16", 437)]
    [InlineData("CLIENT:PMDG_NG3_Data@0x4E473331:16", PmdgNg3AreaId)]
    public void Unknown_areas_must_publish_their_area_id(string input, int expectedAreaId)
    {
        var reference = SimVarParser.ParseClientReference(input);

        Assert.NotNull(reference);
        Assert.Equal(expectedAreaId, reference!.ClientAreaId);
        Assert.Equal(16, reference.ClientOffset);
    }

    [Theory]
    [InlineData("CLIENT:UNKNOWN_AREA:16")]     // not in KnownClientDataAreas and no @ID
    [InlineData("CLIENT:MY_AREA@notanumber:16")]
    [InlineData("CLIENT:MY_AREA@:16")]
    [InlineData("CLIENT:PMDG_NG3_Data")]        // no offset
    [InlineData("CLIENT:PMDG_NG3_Data:")]       // empty offset
    [InlineData("CLIENT:PMDG_NG3_Data:-1")]     // negative offset
    [InlineData("CLIENT:PMDG_NG3_Data:0x1B5:nonsense")] // unknown type
    [InlineData("CLIENT::0x1B5")]               // no area name
    [InlineData("CLIENT:")]
    [InlineData("PLANE ALTITUDE")]
    [InlineData("")]
    [InlineData("   ")]
    public void Malformed_references_return_null(string input)
    {
        Assert.Null(SimVarParser.ParseClientReference(input));
    }

    // The SIMCONNECT_CLIENTDATATYPE_* values are static readonly, not const, so the table is
    // supplied through MemberData rather than InlineData.
    public static TheoryData<string, string, uint> TypeTokens => new()
    {
        { "bool", "byte", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT8 },
        { "byte", "byte", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT8 },
        { "uint8", "byte", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT8 },
        { "int8", "int8", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT8 },
        { "int16", "int16", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT16 },
        { "short", "int16", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT16 },
        { "uint16", "uint16", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT16 },
        { "ushort", "uint16", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT16 },
        { "word", "uint16", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT16 },
        { "int32", "int32", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT32 },
        { "int", "int32", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT32 },
        { "uint32", "uint32", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT32 },
        { "uint", "uint32", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT32 },
        { "dword", "uint32", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT32 },
        { "int64", "int64", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT64 },
        { "long", "int64", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT64 },
        { "uint64", "uint64", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT64 },
        { "ulong", "uint64", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT64 },
        { "float", "float32", SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT32 },
        { "float32", "float32", SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT32 },
        { "double", "float64", SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT64 },
        { "float64", "float64", SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT64 },
        { "BOOL", "byte", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT8 },
        { " Int32 ", "int32", SimConnect.SIMCONNECT_CLIENTDATATYPE_INT32 },
    };

    [Theory]
    [MemberData(nameof(TypeTokens))]
    public void Type_token_maps_to_a_client_data_type(string typeToken, string expectedName, uint expectedSizeOrType)
    {
        var reference = SimVarParser.ParseClientReference($"CLIENT:PMDG_NG3_Data:0x1B5:{typeToken}");

        Assert.NotNull(reference);
        Assert.Equal(expectedName, reference!.ClientTypeName);
        Assert.Equal(expectedSizeOrType, reference.ClientSizeOrType);
    }

    [Fact]
    public void Alias_comes_from_the_pipe_and_defaults_to_area_and_offset()
    {
        var aliased = SimVarParser.ParseClientReference("CLIENT:PMDG_NG3_Data:0x1B5:bool|AT_ARM");
        Assert.Equal("AT_ARM", aliased!.OutputAlias);
        Assert.Equal("AT_ARM", aliased.GetOutputName());

        var bare = SimVarParser.ParseClientReference("CLIENT:PMDG_NG3_Data:0x1B5:bool");
        Assert.Equal("PMDG_NG3_Data:437", bare!.OutputAlias);

        // An empty alias after the pipe falls back to the default, it does not become "".
        var emptyAlias = SimVarParser.ParseClientReference("CLIENT:PMDG_NG3_Data:0x1B5:bool|");
        Assert.Equal("PMDG_NG3_Data:437", emptyAlias!.OutputAlias);
    }

    [Fact]
    public void CreateReference_routes_client_names_to_the_client_parser()
    {
        var reference = SimVarParser.CreateReference("CLIENT:PMDG_NG3_Data:0x1B5:bool|AT_ARM");

        Assert.True(reference.IsClientData);
        Assert.Equal(PmdgNg3AreaId, reference.ClientAreaId);
        Assert.Equal(0x1B5, reference.ClientOffset);
        Assert.Equal("AT_ARM", reference.OutputAlias);
    }

    [Fact]
    public void CreateReference_keeps_the_marker_for_an_invalid_client_name()
    {
        // The name is kept as-is so the request is rejected as a client reference rather than
        // handed to SimConnect as a SimVar of the same name.
        var reference = SimVarParser.CreateReference("CLIENT:BOGUS_AREA:0x10");

        Assert.True(reference.IsClientData);
        Assert.Equal("CLIENT:BOGUS_AREA:0x10", reference.SimVarName);
        Assert.Equal(-1, reference.ClientOffset);   // the guard in ClientDataService tests for this
        Assert.Equal(0, reference.ClientAreaId);    // ... and for this
        Assert.Equal("invalid", reference.ClientTypeName);
    }

    [Fact]
    public void ClientKey_identifies_the_field_not_the_alias()
    {
        var first = SimVarParser.CreateReference("CLIENT:PMDG_NG3_Data:0x1B5:bool|AT_ARM");
        var second = SimVarParser.CreateReference("CLIENT:PMDG_NG3_Data:0x1B5:bool|ARM_SWITCH");
        var other = SimVarParser.CreateReference("CLIENT:PMDG_NG3_Data:0x1B6:bool|AT_ARM");

        Assert.Equal(first.ClientKey(), second.ClientKey());
        Assert.NotEqual(first.ClientKey(), other.ClientKey());
    }

    [Fact]
    public void NormalizeReference_leaves_client_references_alone()
    {
        var reference = SimVarParser.CreateReference("CLIENT:PMDG_NG3_Data:0x1B5:bool|AT_ARM");

        Assert.Same(reference, SimVarParser.NormalizeReference(reference));
    }

    [Fact]
    public void NormalizeReference_converts_a_client_name_to_a_client_reference()
    {
        var reference = new SimVarReference { SimVarName = "CLIENT:PMDG_NG3_Data:0x1B5:bool", Unit = "feet" };

        var normalized = SimVarParser.NormalizeReference(reference);

        Assert.True(normalized.IsClientData);
        Assert.Equal(PmdgNg3AreaId, normalized.ClientAreaId);
        Assert.Equal(0x1B5, normalized.ClientOffset);
        Assert.Equal(string.Empty, normalized.Unit);
    }

    [Fact]
    public void NormalizeReference_marks_an_invalid_client_name()
    {
        var reference = new SimVarReference { SimVarName = "CLIENT:BOGUS_AREA:0x10" };

        var normalized = SimVarParser.NormalizeReference(reference);

        Assert.True(normalized.IsClientData);
        Assert.Equal(-1, normalized.ClientOffset);
        Assert.Equal("invalid", normalized.ClientTypeName);
    }

    [Fact]
    public void NormalizeReference_splits_unit_and_alias_out_of_a_simvar_name()
    {
        var reference = new SimVarReference { SimVarName = "PLANE ALTITUDE[feet]|ALT", Unit = "knots" };

        var normalized = SimVarParser.NormalizeReference(reference);

        Assert.Equal("PLANE ALTITUDE", normalized.SimVarName);
        Assert.Equal("feet", normalized.Unit);
        Assert.Equal("ALT", normalized.OutputAlias);
    }

    [Fact]
    public void NormalizeReference_returns_a_plain_simvar_untouched()
    {
        var reference = new SimVarReference { SimVarName = "PLANE ALTITUDE", Unit = "feet" };

        Assert.Same(reference, SimVarParser.NormalizeReference(reference));
    }
}
