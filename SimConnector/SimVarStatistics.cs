using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SimConnector
{
    /// <summary>
    /// Lookup counters for one variable, as shown by the status page.
    /// </summary>
    public record SimVarStatEntry
    {
        /// <summary>Name as requested, including the unit or the client data offset.</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>"SimVar" or "Client data".</summary>
        public string Kind { get; init; } = "SimVar";

        public long Reads { get; init; }
        public long Writes { get; init; }

        /// <summary>Requests that came back without a usable value (timeout, missing area, not connected).</summary>
        public long NoValue { get; init; }

        /// <summary>Last value seen, from a read or a write; null when there never was one.</summary>
        public double? LastValue { get; init; }

        public DateTime? LastReadUtc { get; init; }
        public DateTime? LastWriteUtc { get; init; }
    }

    /// <summary>
    /// Thread-safe counters for every variable that passes through <see cref="SimVarService"/>.
    /// Counting there means the numbers cover HTTP, WebSocket, UDP and serial lookups alike.
    /// </summary>
    public class SimVarStatistics
    {
        private sealed class Counters
        {
            public string Name = string.Empty;
            public string Kind = "SimVar";
            public long Reads;
            public long Writes;
            public long NoValue;
            public double? LastValue;
            public DateTime? LastReadUtc;
            public DateTime? LastWriteUtc;
        }

        private readonly ConcurrentDictionary<string, Counters> _variables = new(StringComparer.OrdinalIgnoreCase);
        private long _totalReads;
        private long _totalWrites;
        private long _totalNoValue;

        /// <summary>When counting started (process start).</summary>
        public DateTime StartedUtc { get; } = DateTime.UtcNow;

        public long TotalReads => Interlocked.Read(ref _totalReads);
        public long TotalWrites => Interlocked.Read(ref _totalWrites);
        public long TotalNoValue => Interlocked.Read(ref _totalNoValue);

        /// <summary>Records one read, whether it produced a value or not.</summary>
        public void RecordRead(SimVarReference? reference, SimVarReference? result)
        {
            if (reference == null) return;

            var counters = Get(reference);
            Interlocked.Increment(ref _totalReads);
            Interlocked.Increment(ref counters.Reads);
            counters.LastReadUtc = DateTime.UtcNow;

            if (HasValue(result))
            {
                counters.LastValue = result!.Value;
            }
            else
            {
                Interlocked.Increment(ref _totalNoValue);
                Interlocked.Increment(ref counters.NoValue);
            }
        }

        /// <summary>Records one write, whether it was confirmed or not.</summary>
        public void RecordWrite(SimVarReference? reference, SimVarReference? result)
        {
            if (reference == null) return;

            var counters = Get(reference);
            Interlocked.Increment(ref _totalWrites);
            Interlocked.Increment(ref counters.Writes);
            counters.LastWriteUtc = DateTime.UtcNow;

            if (result != null)
            {
                counters.LastValue = result.Value;
            }
            else
            {
                Interlocked.Increment(ref _totalNoValue);
                Interlocked.Increment(ref counters.NoValue);
            }
        }

        /// <summary>Current counters, most used variables first.</summary>
        public List<SimVarStatEntry> Snapshot()
        {
            return _variables.Values
                .Select(c => new SimVarStatEntry
                {
                    Name = c.Name,
                    Kind = c.Kind,
                    Reads = Interlocked.Read(ref c.Reads),
                    Writes = Interlocked.Read(ref c.Writes),
                    NoValue = Interlocked.Read(ref c.NoValue),
                    LastValue = c.LastValue,
                    LastReadUtc = c.LastReadUtc,
                    LastWriteUtc = c.LastWriteUtc,
                })
                .OrderByDescending(e => e.Reads + e.Writes)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>Drops every counter.</summary>
        public void Reset()
        {
            _variables.Clear();
            Interlocked.Exchange(ref _totalReads, 0);
            Interlocked.Exchange(ref _totalWrites, 0);
            Interlocked.Exchange(ref _totalNoValue, 0);
        }

        /// <summary>
        /// The key a reference is counted under: name plus unit, so the same SimVar in two units stays apart.
        /// Client data references carry area, offset and type in the name already.
        /// </summary>
        public static string KeyOf(SimVarReference reference)
        {
            if (reference.IsClientData)
            {
                // SimVarName holds the area name for client data, so the field has to be added:
                // otherwise every offset in one area would share a counter.
                var offset = reference.ClientOffset >= 0
                    ? "0x" + reference.ClientOffset.ToString("X")
                    : reference.ClientOffset.ToString();
                return $"CLIENT:{reference.SimVarName}:{offset}:{reference.ClientTypeName}";
            }

            var name = reference.SimVarName ?? string.Empty;
            return string.IsNullOrEmpty(reference.Unit) ? name : name + "[" + reference.Unit + "]";
        }

        private static bool HasValue(SimVarReference? result)
        {
            return result != null && !double.IsNaN(result.Value) && !double.IsInfinity(result.Value);
        }

        private Counters Get(SimVarReference reference)
        {
            var key = KeyOf(reference);
            return _variables.GetOrAdd(key, _ => new Counters
            {
                Name = key,
                Kind = reference.IsClientData ? "Client data" : "SimVar",
            });
        }
    }
}
