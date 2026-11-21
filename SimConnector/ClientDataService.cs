using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace SimConnector
{
    /// <summary>
    /// Reads single fields from SimConnect client data areas (shared data broadcast by add-ons
    /// such as PMDG). This is a separate path from the SimVar reader: client data is addressed
    /// by area name + byte offset + type, never by a SimVar name.
    ///
    /// Every field is subscribed once with SIMCONNECT_CLIENT_DATA_PERIOD_ON_SET; the latest
    /// value is cached and reads are served from the cache, so a 100 ms streaming loop does not
    /// issue a SimConnect round trip per field per tick. The first read of a field (or a read
    /// after the cache went stale) is primed with a one-shot request.
    ///
    /// Implementation notes (verified against MSFS 2024 + PMDG 737):
    /// - The managed wrapper exposes the reply as a single 32-bit word in dwData, whatever the
    ///   definition type or size. Fields larger than 4 bytes are read as two 4-byte chunks.
    /// - Adding several fields to one definition makes SimConnect reject the definition with
    ///   SIMCONNECT_EXCEPTION_DUPLICATE_ID, so each field gets its own definition.
    /// - RegisterDataDefineStruct has no effect on client data replies.
    /// </summary>
    public class ClientDataService
    {
        private readonly SimConnectClient _connection;
        private readonly ILogger _logger;

        // Area name -> SimConnect client data ID we have already mapped on this connection.
        private readonly ConcurrentDictionary<string, int> _mappedAreas = new();
        // Chunk key (areaId:offset:size) -> define ID used for that chunk.
        private readonly ConcurrentDictionary<string, int> _defineIds = new();
        // Cache key -> latest word received (from a subscription or a priming request).
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
        // Request ID -> subscription that feeds the cache.
        private readonly ConcurrentDictionary<uint, Subscription> _subscriptions = new();
        // Subscription key -> already subscribed marker.
        private readonly ConcurrentDictionary<string, byte> _subscribed = new();
        // Request ID -> pending one-shot priming request.
        private readonly ConcurrentDictionary<uint, PendingChunk> _pending = new();

        // Serialises registration of areas, definitions and subscriptions.
        private readonly object _registrationLock = new();

        // Client data IDs start well above the SimVar IDs used by SimVarService so the
        // two services cannot hand SimConnect the same request or definition number.
        private int _nextDefineId = 100000;
        private int _nextRequestId = 100000;

        private const int RequestTimeoutMs = 2000;

        /// <summary>
        /// How long a cached value is trusted. Add-ons that broadcast continuously (PMDG does)
        /// keep it fresh via the subscription; a value that has not been refreshed within this
        /// window is re-read with a one-shot request.
        /// </summary>
        private const int CacheMaxAgeMs = 2000;

        /// <summary>
        /// How long one chunk of a raw byte range (see <see cref="GetRawBytesAsync"/>) is trusted.
        /// Longer than a single field's window on purpose: a screen is read as 250+ chunks, and
        /// re-reading all of them because nothing was written to the area for a second would cost
        /// far more than the stale value it fixes. Data that does change still arrives through the
        /// ON_SET subscription within milliseconds.
        /// </summary>
        private const int RawCacheMaxAgeMs = 10000;

        /// <summary>Largest byte range a single read may cover, so one request cannot fan out.</summary>
        public const int MaxRawRangeLength = 8192;

        private readonly struct CacheEntry
        {
            public CacheEntry(uint word, DateTime timestamp) { Word = word; Timestamp = timestamp; }
            public uint Word { get; }
            public DateTime Timestamp { get; }
        }

        private class Subscription
        {
            public string CacheKey { get; init; } = string.Empty;
        }

        private class PendingChunk
        {
            public TaskCompletionSource<uint?> TaskCompletion { get; set; } = new();
        }

        public ClientDataService(SimConnectClient connection, ILogger logger)
        {
            _connection = connection;
            _logger = logger;

            _connection.OnRecvClientData += OnRecvClientData;
            _connection.OnRecvException += (sender, data) =>
            {
                _logger.LogWarning("SimConnect exception while working with client data: {ExceptionId}", data.dwException);
            };
            _connection.OnDisconnected += () =>
            {
                // Definitions, subscriptions and name mappings live on the SimConnect
                // connection; drop them so the next read re-registers everything.
                lock (_registrationLock)
                {
                    _mappedAreas.Clear();
                    _defineIds.Clear();
                    _subscriptions.Clear();
                    _subscribed.Clear();
                    _cache.Clear();
                    _pending.Clear();
                }
            };
        }

        private void OnRecvClientData(SimConnect sender, SIMCONNECT_RECV_CLIENT_DATA data)
        {
            uint word = data.dwData != null && data.dwData.Length > 0 ? ToWord(data.dwData[0]) : 0;

            if (_subscriptions.TryGetValue(data.dwRequestID, out var subscription))
            {
                _cache[subscription.CacheKey] = new CacheEntry(word, DateTime.UtcNow);
                return;
            }

            if (_pending.TryRemove(data.dwRequestID, out var chunk))
            {
                chunk.TaskCompletion.TrySetResult(word);
                return;
            }

            _logger.LogDebug("Client data received for unknown request id {RequestId}", data.dwRequestID);
        }

        /// <summary>
        /// The wrapper boxes the payload as a 32-bit word, but the boxed type is not guaranteed
        /// (UInt32 seen in practice), so normalise defensively.
        /// </summary>
        internal static uint ToWord(object value)
        {
            return value switch
            {
                uint u => u,
                int i => unchecked((uint)i),
                long l => unchecked((uint)l),
                ulong ul => unchecked((uint)ul),
                short s => unchecked((uint)(ushort)s),
                ushort us => us,
                byte b => b,
                sbyte sb => unchecked((uint)(byte)sb),
                _ => 0u
            };
        }

        /// <summary>Size in bytes for a SIMCONNECT_CLIENTDATATYPE_* constant.</summary>
        internal static int SizeOf(uint sizeOrType)
        {
            if (sizeOrType == SimConnect.SIMCONNECT_CLIENTDATATYPE_INT8) return 1;
            if (sizeOrType == SimConnect.SIMCONNECT_CLIENTDATATYPE_INT16) return 2;
            if (sizeOrType == SimConnect.SIMCONNECT_CLIENTDATATYPE_INT32) return 4;
            if (sizeOrType == SimConnect.SIMCONNECT_CLIENTDATATYPE_INT64) return 8;
            if (sizeOrType == SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT32) return 4;
            if (sizeOrType == SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT64) return 8;

            // A raw byte count was given.
            return (int)sizeOrType;
        }

        internal static double DecodeWord(uint word, uint sizeOrType, string typeName)
        {
            switch (typeName)
            {
                case "byte": return word & 0xFF;                            // unsigned char / bool
                case "int8": return unchecked((sbyte)(word & 0xFF));        // signed char
                case "int16": return unchecked((short)(word & 0xFFFF));     // short
                case "uint16": return word & 0xFFFF;                        // unsigned short
                case "int32": return unchecked((int)word);
                case "uint32": return word;
                case "float32": return BitConverter.Int32BitsToSingle(unchecked((int)word));
            }

            // A raw byte count: keep the low bytes as an unsigned integer.
            int size = SizeOf(sizeOrType);
            return size >= 4 ? word : word & ((1u << (size * 8)) - 1);
        }

        internal static double DecodeTwoWords(uint low, uint high, string typeName)
        {
            ulong bits = ((ulong)high << 32) | low;
            return typeName switch
            {
                "float64" => BitConverter.Int64BitsToDouble(unchecked((long)bits)),
                "uint64" => bits,
                _ => unchecked((long)bits),
            };
        }

        /// <summary>
        /// The chunks SimConnect is asked for when a byte range is read. The managed wrapper
        /// returns one 32-bit word per definition, so a chunk is four bytes wide, and the last one
        /// is shorter when the length is not a multiple of four - a definition that reached past
        /// the end of the area would be rejected with SIMCONNECT_EXCEPTION_OUT_OF_BOUNDS and never
        /// answered (the PMDG CDU area is 1009 bytes: 1008 of cells plus the Powered bool).
        /// </summary>
        internal static (int Offset, int Size)[] ChunkRanges(int offset, int length)
        {
            const int chunkSize = 4;
            var chunks = new List<(int, int)>((length + chunkSize - 1) / chunkSize);
            int at = offset;
            int remaining = length;
            while (remaining > 0)
            {
                int size = Math.Min(chunkSize, remaining);
                chunks.Add((at, size));
                at += size;
                remaining -= size;
            }
            return chunks.ToArray();
        }

        /// <summary>
        /// Little-endian assembly of the words SimConnect returned for a range: the low byte of a
        /// word is the area byte at that definition's offset (the offset need not be aligned).
        /// </summary>
        internal static byte[] WordsToBytes(IReadOnlyList<uint> words, IReadOnlyList<int> sizes)
        {
            int total = 0;
            for (int i = 0; i < sizes.Count; i++) total += sizes[i];

            var bytes = new byte[total];
            int at = 0;
            for (int i = 0; i < words.Count; i++)
            {
                for (int b = 0; b < sizes[i]; b++)
                {
                    bytes[at++] = (byte)(words[i] >> (8 * b));
                }
            }
            return bytes;
        }

        /// <summary>A reference to a byte range of an area, used for the chunk definitions and keys.</summary>
        private static SimVarReference AreaReference(string areaName, int areaId, int offset, int size) => new()
        {
            SimVarName = areaName,
            IsClientData = true,
            ClientAreaId = areaId,
            ClientOffset = offset,
            ClientSizeOrType = (uint)size,
            ClientTypeName = "raw"
        };

        /// <summary>
        /// True when a reference addresses a field that can be read. A malformed CLIENT: name
        /// carries the placeholder offset -1 / area 0 from the parser, and has to be rejected
        /// here instead of being sent to SimConnect.
        /// </summary>
        internal static bool IsReadable(SimVarReference? reference)
        {
            return reference is { IsClientData: true, ClientOffset: >= 0, ClientAreaId: not 0 };
        }

        /// <summary>
        /// Client data areas mapped on this connection so far, as area name to SimConnect data area ID.
        /// The status page lists them.
        /// </summary>
        public IReadOnlyDictionary<string, int> GetKnownAreas()
        {
            return new Dictionary<string, int>(_mappedAreas);
        }

        public async Task<SimVarReference?> GetClientDataValueAsync(SimVarReference reference)
        {
            if (reference == null || !reference.IsClientData) return null;
            if (!IsReadable(reference))
            {
                _logger.LogWarning(
                    "Invalid client data reference '{Name}' - expected CLIENT:AREA[:@AREA_ID]:OFFSET[:TYPE]|ALIAS",
                    reference.SimVarName);
                return null;
            }
            if (!_connection.IsConnected)
            {
                _logger.LogDebug("SimConnect not connected, cannot read client data.");
                return null;
            }

            string fieldKey = reference.ClientKey();
            int size = SizeOf(reference.ClientSizeOrType);

            if (size <= 4)
            {
                EnsureSubscribed(fieldKey, fieldKey, reference, reference.ClientOffset, size);

                if (TryGetCached(fieldKey, out uint word))
                    return reference with { Value = DecodeWord(word, reference.ClientSizeOrType, reference.ClientTypeName) };

                uint? primed = await RequestOnceAsync(reference, reference.ClientOffset, size);
                if (primed == null) return Unavailable(reference);
                _cache[fieldKey] = new CacheEntry(primed.Value, DateTime.UtcNow);
                return reference with { Value = DecodeWord(primed.Value, reference.ClientSizeOrType, reference.ClientTypeName) };
            }

            // Wider than the single word the wrapper hands back: two chunks, two subscriptions.
            string lowKey = fieldKey + "#low";
            string highKey = fieldKey + "#high";
            EnsureSubscribed(fieldKey, lowKey, reference, reference.ClientOffset, 4);
            EnsureSubscribed(fieldKey, highKey, reference, reference.ClientOffset + 4, 4);

            bool haveLow = TryGetCached(lowKey, out uint low);
            bool haveHigh = TryGetCached(highKey, out uint high);

            if (!haveLow || !haveHigh)
            {
                Task<uint?> lowTask = haveLow ? Task.FromResult<uint?>(low) : RequestOnceAsync(reference, reference.ClientOffset, 4);
                Task<uint?> highTask = haveHigh ? Task.FromResult<uint?>(high) : RequestOnceAsync(reference, reference.ClientOffset + 4, 4);
                await Task.WhenAll(lowTask, highTask);

                uint? primedLow = await lowTask;
                uint? primedHigh = await highTask;
                if (primedLow == null || primedHigh == null) return Unavailable(reference);

                _cache[lowKey] = new CacheEntry(primedLow.Value, DateTime.UtcNow);
                _cache[highKey] = new CacheEntry(primedHigh.Value, DateTime.UtcNow);
                low = primedLow.Value;
                high = primedHigh.Value;
            }

            return reference with { Value = DecodeTwoWords(low, high, reference.ClientTypeName) };
        }

        /// <summary>
        /// Reads a byte range of a client data area as raw bytes - the CDU screen of the PMDG 737
        /// is the use case: one <c>PMDG_NG3_CDU_Screen</c>, 24x14 cells of symbol/color/flags
        /// followed by a Powered bool, 1009 bytes that no single SimVar or field read can carry.
        ///
        /// The range is covered by 4-byte chunks (see <see cref="ChunkRanges"/>), each subscribed
        /// once exactly like a single field, so a page streaming the screen is served from the
        /// cache instead of a SimConnect round trip per chunk. Returns null when the area is not
        /// being broadcast (add-on not loaded, broadcast disabled) or a chunk cannot be read.
        /// </summary>
        public async Task<byte[]?> GetRawBytesAsync(string areaName, int areaId, int offset, int length)
        {
            if (string.IsNullOrWhiteSpace(areaName) || areaId == 0 || offset < 0 || length <= 0 || length > MaxRawRangeLength)
            {
                _logger.LogWarning(
                    "Invalid client data range request: area '{Area}' id {AreaId} offset {Offset} length {Length}",
                    areaName, areaId, offset, length);
                return null;
            }
            if (!_connection.IsConnected)
            {
                _logger.LogDebug("SimConnect not connected, cannot read client data range.");
                return null;
            }

            // One byte of the range as a presence check: an area that is not broadcast then costs a
            // single request that times out, instead of one per chunk.
            var probe = await GetClientDataValueAsync(AreaReference(areaName, areaId, offset, 1));
            if (probe == null || double.IsNaN(probe.Value)) return null;

            var chunks = ChunkRanges(offset, length);
            var words = new uint[chunks.Length];
            var sizes = new int[chunks.Length];
            var missing = new List<int>();

            for (int i = 0; i < chunks.Length; i++)
            {
                var (chunkOffset, chunkSize) = chunks[i];
                sizes[i] = chunkSize;
                var reference = AreaReference(areaName, areaId, chunkOffset, chunkSize);
                string key = reference.ClientKey();

                EnsureSubscribed(key, key, reference, chunkOffset, chunkSize);

                if (TryGetCached(key, out uint word, RawCacheMaxAgeMs)) words[i] = word;
                else missing.Add(i);
            }

            if (missing.Count > 0)
            {
                var reads = missing
                    .Select(i => RequestOnceAsync(
                        AreaReference(areaName, areaId, chunks[i].Offset, chunks[i].Size),
                        chunks[i].Offset,
                        chunks[i].Size))
                    .ToArray();
                await Task.WhenAll(reads);

                for (int r = 0; r < reads.Length; r++)
                {
                    uint? value = await reads[r];
                    if (value == null)
                    {
                        _logger.LogWarning(
                            "No client data received for {Area} chunk at offset 0x{Offset:X} ({Size} bytes)",
                            areaName, chunks[missing[r]].Offset, chunks[missing[r]].Size);
                        return null;
                    }

                    var (chunkOffset, chunkSize) = chunks[missing[r]];
                    string key = AreaReference(areaName, areaId, chunkOffset, chunkSize).ClientKey();
                    _cache[key] = new CacheEntry(value.Value, DateTime.UtcNow);
                    words[missing[r]] = value.Value;
                }
            }

            return WordsToBytes(words, sizes);
        }

        private bool TryGetCached(string cacheKey, out uint word) => TryGetCached(cacheKey, out word, CacheMaxAgeMs);

        private bool TryGetCached(string cacheKey, out uint word, int maxAgeMs)
        {
            word = 0;
            if (!_cache.TryGetValue(cacheKey, out var entry)) return false;
            if (DateTime.UtcNow - entry.Timestamp > TimeSpan.FromMilliseconds(maxAgeMs)) return false;
            word = entry.Word;
            return true;
        }

        /// <summary>
        /// Maps the area, defines the chunk and subscribes to it once per connection.
        /// Everything here is registration only; the cache is filled by the subscription.
        /// </summary>
        private void EnsureSubscribed(string fieldKey, string cacheKey, SimVarReference reference, int offset, int size)
        {
            lock (_registrationLock)
            {
                if (!_mappedAreas.TryGetValue(reference.SimVarName, out _))
                {
                    _connection.MapClientDataArea(reference.SimVarName, reference.ClientAreaId);
                    _mappedAreas[reference.SimVarName] = reference.ClientAreaId;
                }

                string chunkKey = $"{reference.ClientAreaId}:{offset}:{size}";
                if (!_defineIds.TryGetValue(chunkKey, out int defineId))
                {
                    defineId = System.Threading.Interlocked.Increment(ref _nextDefineId);
                    _connection.AddClientDataDefinition(defineId, offset, (uint)size);
                    _defineIds[chunkKey] = defineId;
                }

                string subscriptionKey = $"{fieldKey}:{cacheKey}";
                if (_subscribed.TryAdd(subscriptionKey, 0))
                {
                    int requestId = System.Threading.Interlocked.Increment(ref _nextRequestId);
                    _subscriptions[(uint)requestId] = new Subscription { CacheKey = cacheKey };
                    _connection.RequestClientDataSubscription(reference.ClientAreaId, requestId, defineId);
                    _logger.LogDebug("Subscribed to client data {Area} at offset 0x{Offset:X}", reference.SimVarName, offset);
                }
            }
        }

        private SimVarReference Unavailable(SimVarReference reference)
        {
            _logger.LogWarning(
                "No client data received for {Area} at offset 0x{Offset:X} (add-on loaded and broadcasting?)",
                reference.SimVarName, reference.ClientOffset);
            return reference with { Value = double.NaN };
        }

        /// <summary>
        /// One-shot read used to prime the cache for a field that has no fresh value yet.
        /// </summary>
        private async Task<uint?> RequestOnceAsync(SimVarReference reference, int offset, int size)
        {
            int defineId;
            lock (_registrationLock)
            {
                string chunkKey = $"{reference.ClientAreaId}:{offset}:{size}";
                if (!_defineIds.TryGetValue(chunkKey, out defineId))
                {
                    defineId = System.Threading.Interlocked.Increment(ref _nextDefineId);
                    _connection.AddClientDataDefinition(defineId, offset, (uint)size);
                    _defineIds[chunkKey] = defineId;
                }
            }

            int requestId = System.Threading.Interlocked.Increment(ref _nextRequestId);
            var pending = new PendingChunk();
            _pending[(uint)requestId] = pending;

            _connection.RequestClientDataOnce(reference.ClientAreaId, requestId, defineId);

            var timeoutTask = Task.Delay(RequestTimeoutMs);
            var completed = await Task.WhenAny(pending.TaskCompletion.Task, timeoutTask);
            if (completed == timeoutTask)
            {
                _pending.TryRemove((uint)requestId, out _);
                return null;
            }

            return await pending.TaskCompletion.Task;
        }
    }
}
