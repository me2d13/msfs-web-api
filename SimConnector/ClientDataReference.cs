using System.Collections.Generic;

namespace SimConnector
{
    /// <summary>
    /// Client data areas whose published numeric IDs are known to this server.
    /// The ID must match the one used by the add-on that created the area,
    /// otherwise SimConnect rejects the name mapping.
    /// </summary>
    public static class KnownClientDataAreas
    {
        private static readonly Dictionary<string, int> Areas = new(StringComparer.OrdinalIgnoreCase)
        {
            { "PMDG_NG3_Data", 0x4E473331 },
        };

        public static bool TryGetId(string areaName, out int id)
        {
            return Areas.TryGetValue(areaName, out id);
        }
    }
}
