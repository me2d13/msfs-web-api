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
            // One CDU screen each, a PMDG_NG3_CDU_Screen: 24x14 cells of { Symbol, Color, Flags }
            // followed by a Powered bool (byte 1008). Broadcast only when the aircraft's
            // 737_Options.ini has [SDK] EnableCDUBroadcast.0=1 / .1=1.
            { "PMDG_NG3_CDU_0", 0x4E473335 },
            { "PMDG_NG3_CDU_1", 0x4E473336 },
        };

        public static bool TryGetId(string areaName, out int id)
        {
            return Areas.TryGetValue(areaName, out id);
        }
    }
}
