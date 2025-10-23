using System.Collections.Concurrent;
using System.Threading;

namespace SimConnector
{
    /// <summary>
    /// Manages unique IDs for SimVar definitions and requests for SimConnect operations.
    /// </summary>
    public class SimConnectIdManager
    {
        // Thread-safe counter for generating unique sequential IDs
        private static int nextId = 0;

        // Maps SimVarReference to unique integer ID (for SimConnect DefineID/RequestID)
        private readonly ConcurrentDictionary<SimVarReference, int> _definitionToId =
            new ConcurrentDictionary<SimVarReference, int>();

        // Maps unique integer ID to SimVarReference (for lookup upon reception)
        private readonly ConcurrentDictionary<int, SimVarReference> _idToDefinition =
            new ConcurrentDictionary<int, SimVarReference>();

        /// <summary>
        /// Gets or assigns a unique ID for the given SimVarReference.
        /// </summary>
        /// <param name="reference">The SimVar reference.</param>
        /// <returns>A tuple of (id, isNew) where isNew is true if a new ID was assigned.</returns>
        public (int id, bool isNew) GetOrAssignId(SimVarReference reference)
        {
            int newId = Interlocked.Increment(ref nextId);
            if (_definitionToId.TryAdd(reference, newId))
            {
                _idToDefinition.TryAdd(newId, reference);
                return (newId, true);
            }
            else
            {
                return (_definitionToId[reference], false);
            }
        }

        /// <summary>
        /// Tries to get the SimVarReference associated with a given ID.
        /// </summary>
        /// <param name="id">The unique ID.</param>
        /// <param name="reference">The SimVarReference if found.</param>
        /// <returns>True if found, otherwise false.</returns>
        public bool TryGetReference(int id, out SimVarReference reference)
        {
            return _idToDefinition.TryGetValue(id, out reference);
        }

        /// <summary>
        /// Clears all registered IDs. Used when SimConnect connection is lost.
        /// </summary>
        public void Clear()
        {
            _definitionToId.Clear();
            _idToDefinition.Clear();
            nextId = 0;
        }
    }
}
