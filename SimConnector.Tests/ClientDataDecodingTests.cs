using Microsoft.FlightSimulator.SimConnect;
using SimConnector;

namespace SimConnector.Tests;

/// <summary>
/// The client data decoding rules. The managed wrapper hands back a single 32-bit word per
/// definition whatever the declared type, so these helpers do the actual value conversion:
/// masking and sign extension for narrow types, bit reinterpretation for floats, and
/// little-endian assembly of two words for 64-bit fields.
/// </summary>
public class ClientDataDecodingTests
{
    // The SIMCONNECT_CLIENTDATATYPE_* values are static readonly, not const, so the table is
    // supplied through MemberData rather than InlineData.
    public static TheoryData<uint, int> TypeSizes => new()
    {
        { SimConnect.SIMCONNECT_CLIENTDATATYPE_INT8, 1 },
        { SimConnect.SIMCONNECT_CLIENTDATATYPE_INT16, 2 },
        { SimConnect.SIMCONNECT_CLIENTDATATYPE_INT32, 4 },
        { SimConnect.SIMCONNECT_CLIENTDATATYPE_INT64, 8 },
        { SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT32, 4 },
        { SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT64, 8 },
    };

    [Theory]
    [MemberData(nameof(TypeSizes))]
    public void SizeOf_maps_the_type_constants(uint sizeOrType, int expectedSize)
    {
        Assert.Equal(expectedSize, ClientDataService.SizeOf(sizeOrType));
    }

    [Theory]
    [InlineData(1u, 1)]
    [InlineData(3u, 3)]
    [InlineData(7u, 7)]
    public void SizeOf_treats_anything_else_as_a_raw_byte_count(uint sizeOrType, int expectedSize)
    {
        Assert.Equal(expectedSize, ClientDataService.SizeOf(sizeOrType));
    }

    [Theory]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x00000000u)]
    [InlineData(0x000000FFu)]
    public void ToWord_passes_unsigned_values_through(uint value)
    {
        Assert.Equal(value, ClientDataService.ToWord(value));
    }

    [Fact]
    public void ToWord_normalises_the_boxed_integer_types()
    {
        Assert.Equal(0xFFFFFFFFu, ClientDataService.ToWord(-1));            // int
        Assert.Equal(0x00000003u, ClientDataService.ToWord(0x1_0000_0003L)); // long, truncated
        Assert.Equal(0xFFFFFFFFu, ClientDataService.ToWord(-1L));            // long
        Assert.Equal(0xFFFFFFFFu, ClientDataService.ToWord(0xFFFFFFFF_FFFFFFFFuL)); // ulong
        Assert.Equal(0x0000FFFFu, ClientDataService.ToWord((short)-1));
        Assert.Equal(0x0000FFFFu, ClientDataService.ToWord((ushort)0xFFFF));
        Assert.Equal(0x000000FFu, ClientDataService.ToWord((byte)0xFF));
        Assert.Equal(0x000000FFu, ClientDataService.ToWord((sbyte)-1));
        Assert.Equal(0u, ClientDataService.ToWord("not a number"));
    }

    [Theory]
    // byte / bool: unsigned char in the add-on SDKs
    [InlineData(0x00000001u, "byte", 1.0)]
    [InlineData(0x000000FFu, "byte", 255.0)]
    [InlineData(0x00000103u, "byte", 3.0)]      // only the low byte counts
    [InlineData(0xFFFFFFFFu, "byte", 255.0)]
    // signed char
    [InlineData(0x000000FFu, "int8", -1.0)]
    [InlineData(0x00000080u, "int8", -128.0)]
    [InlineData(0x0000007Fu, "int8", 127.0)]
    // short / unsigned short
    [InlineData(0x0000FFFFu, "int16", -1.0)]
    [InlineData(0x00008000u, "int16", -32768.0)]
    [InlineData(0x0000FFFFu, "uint16", 65535.0)]
    // 32-bit
    [InlineData(0xFFFFFFFFu, "int32", -1.0)]
    [InlineData(0x80000000u, "int32", -2147483648.0)]
    [InlineData(0xFFFFFFFFu, "uint32", 4294967295.0)]
    // float32: raw bits, not a numeric conversion
    [InlineData(0x42C80000u, "float32", 100.0)]
    [InlineData(0xBF800000u, "float32", -1.0)]
    [InlineData(0x00000000u, "float32", 0.0)]
    public void DecodeWord_converts_a_single_word(uint word, string typeName, double expected)
    {
        uint sizeOrType = typeName switch
        {
            "byte" or "int8" => SimConnect.SIMCONNECT_CLIENTDATATYPE_INT8,
            "int16" or "uint16" => SimConnect.SIMCONNECT_CLIENTDATATYPE_INT16,
            "int32" or "uint32" => SimConnect.SIMCONNECT_CLIENTDATATYPE_INT32,
            _ => SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT32,
        };

        Assert.Equal(expected, ClientDataService.DecodeWord(word, sizeOrType, typeName));
    }

    [Theory]
    [InlineData(0x000001FFu, 1u, 255.0)]   // raw 1-byte range
    [InlineData(0x000001FFu, 2u, 511.0)]   // raw 2-byte range
    [InlineData(0x12345678u, 3u, 0x345678)] // raw 3-byte range
    [InlineData(0x12345678u, 4u, 305419896.0)] // raw 4-byte range: the whole word
    public void DecodeWord_keeps_the_low_bytes_of_a_raw_byte_range(uint word, uint sizeOrType, double expected)
    {
        Assert.Equal(expected, ClientDataService.DecodeWord(word, sizeOrType, "raw"));
    }

    [Theory]
    // double: little-endian, low word first
    [InlineData(0x00000000u, 0x40934800u, "float64", 1234.0)]
    [InlineData(0x00000000u, 0xBFE00000u, "float64", -0.5)]
    [InlineData(0x573DC913u, 0x40923BA0u, "float64", 1166.906582799337)]
    [InlineData(0x00000000u, 0x00000000u, "float64", 0.0)]
    // int64
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu, "int64", -1.0)]
    [InlineData(0x00000002u, 0x00000001u, "int64", 4294967298.0)]
    // uint64
    [InlineData(0x00000002u, 0x00000001u, "uint64", 4294967298.0)]
    public void DecodeTwoWords_assembles_64_bit_values_from_two_words(uint low, uint high, string typeName, double expected)
    {
        Assert.Equal(expected, ClientDataService.DecodeTwoWords(low, high, typeName));
    }

    [Fact]
    public void IsReadable_rejects_the_placeholders_used_for_invalid_references()
    {
        var valid = SimVarParser.CreateReference("CLIENT:PMDG_NG3_Data:0x1B5:bool");
        var invalid = SimVarParser.CreateReference("CLIENT:BOGUS_AREA:0x10");

        Assert.True(ClientDataService.IsReadable(valid));
        Assert.False(ClientDataService.IsReadable(invalid));
        Assert.False(ClientDataService.IsReadable(null));
        Assert.False(ClientDataService.IsReadable(new SimVarReference { SimVarName = "PLANE ALTITUDE" }));
    }
}
