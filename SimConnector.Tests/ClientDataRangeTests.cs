using SimConnector;

namespace SimConnector.Tests;

/// <summary>
/// Byte-range reads of a client data area. The managed wrapper hands back a single 32-bit word per
/// definition whatever its declared size, so a range is covered by 4-byte chunks and the words are
/// assembled here - the two places where an off-by-one byte or a swapped word hides. The PMDG 737
/// CDU screen is the range this exists for: 1009 bytes (24x14 cells of Symbol/Color/Flags plus a
/// Powered bool).
/// </summary>
public class ClientDataRangeTests
{
    private const int CduScreenLength = 1009;

    [Fact]
    public void ChunkRanges_splits_the_cdu_screen_into_four_byte_chunks_with_a_short_tail()
    {
        var chunks = ClientDataService.ChunkRanges(0, CduScreenLength);

        Assert.Equal(253, chunks.Length);
        Assert.Equal((0, 4), chunks[0]);
        Assert.Equal((1004, 4), chunks[^2]);
        Assert.Equal((1008, 1), chunks[^1]);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Size, 1, 4));
    }

    [Fact]
    public void ChunkRanges_covers_exactly_the_requested_range()
    {
        // Chunks are contiguous and stop at the end of the range: a definition that reached past
        // the area is rejected by SimConnect (OUT_OF_BOUNDS) and never answered at all.
        var chunks = ClientDataService.ChunkRanges(0, CduScreenLength);

        int at = 0;
        foreach (var chunk in chunks)
        {
            Assert.Equal(at, chunk.Offset);
            at += chunk.Size;
        }
        Assert.Equal(CduScreenLength, at);
    }

    [Theory]
    [InlineData(0, 4, new[] { "0:4" })]
    [InlineData(3, 5, new[] { "3:4", "7:1" })]
    [InlineData(16, 1, new[] { "16:1" })]
    [InlineData(1008, 1, new[] { "1008:1" })]
    public void ChunkRanges_starts_at_the_requested_offset(int offset, int length, string[] expected)
    {
        var chunks = ClientDataService.ChunkRanges(offset, length);

        Assert.Equal(expected, chunks.Select(c => $"{c.Offset}:{c.Size}").ToArray());
    }

    [Fact]
    public void WordsToBytes_assembles_little_endian_bytes()
    {
        // The low byte of a word is the area byte at that definition's offset.
        Assert.Equal(
            new byte[] { 0x20, 0x00, 0x00, 0x3C },
            ClientDataService.WordsToBytes(new[] { 0x3C000020u }, new[] { 4 }));
    }

    [Fact]
    public void WordsToBytes_keeps_only_the_bytes_a_chunk_asked_for()
    {
        // A short tail chunk must not leak the bytes the word still carries.
        Assert.Equal(
            new byte[] { 0x11, 0x22, 0xFF },
            ClientDataService.WordsToBytes(new[] { 0x00002211u, 0xDEAD00FFu }, new[] { 2, 1 }));
    }

    [Fact]
    public void WordsToBytes_of_the_cdu_screen_keeps_the_cell_stride()
    {
        // 24x14 cells of three bytes: cell n starts at byte 3n, and the last byte of the range is
        // the Powered bool.
        var words = Enumerable.Range(0, 253).Select(i => 0x04030201u + (uint)i).ToArray();
        var sizes = ClientDataService.ChunkRanges(0, CduScreenLength).Select(c => c.Size).ToArray();

        var bytes = ClientDataService.WordsToBytes(words, sizes);

        Assert.Equal(CduScreenLength, bytes.Length);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, bytes.Take(3));   // first cell: symbol, color, flags
        Assert.Equal(0x04, bytes[3]);                                   // second cell's symbol
        Assert.Equal(0xFD, bytes[^1]);                                  // 0x04030201 + 252 -> low byte
    }
}
