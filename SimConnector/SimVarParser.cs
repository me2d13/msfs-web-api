using Microsoft.FlightSimulator.SimConnect;
using System.Globalization;

namespace SimConnector
{
    /// <summary>
    /// Helper for parsing SimVar names with optional output aliases.
  /// Format: "VAR_NAME|OUTPUT_ALIAS" or just "VAR_NAME"
    /// </summary>
    public static class SimVarParser
    {
        /// <summary>
        /// Parse a SimVar string that may contain a bracketed unit and a pipe-delimited alias.
        /// Returns (actualVarName, unit, outputAlias).
        /// Format: "VAR_NAME[UNIT]|OUTPUT_ALIAS" or "VAR_NAME|OUTPUT_ALIAS" or just "VAR_NAME"
        /// </summary>
        public static (string varName, string? unit, string? alias) ParseVarName(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return (string.Empty, null, null);

            string varName = input;
            string? unit = null;
            string? alias = null;

            // Split alias first (at the end of the string, after any units)
            int pipeIndex = input.IndexOf('|');
            if (pipeIndex >= 0)
            {
                varName = input.Substring(0, pipeIndex).Trim();
                var aliasPart = input.Substring(pipeIndex + 1).Trim();
                alias = string.IsNullOrWhiteSpace(aliasPart) ? null : aliasPart;
            }
            else
            {
                varName = input.Trim();
            }

            // Now check varName for bracketed unit: VAR_NAME[UNIT]
            int startBracket = varName.IndexOf('[');
            int endBracket = varName.LastIndexOf(']');

            if (startBracket >= 0 && endBracket > startBracket)
            {
                unit = varName.Substring(startBracket + 1, endBracket - startBracket - 1).Trim();
                varName = varName.Substring(0, startBracket).Trim();
            }

            return (varName, unit, alias);
        }

        /// <summary>
        /// Create a SimVarReference from a string that may contain unit and alias,
        /// preserving fallback unit and value if provided.
        /// </summary>
        public static SimVarReference CreateReference(string varNameWithAlias, string unit = "", double value = 0.0)
        {
            if (IsClientDataName(varNameWithAlias))
            {
                var clientRef = ParseClientReference(varNameWithAlias);
                if (clientRef == null)
                {
                    // Keep the CLIENT: marker so the request is rejected as an invalid
                    // client reference instead of being sent to SimConnect as a SimVar.
                    return new SimVarReference
                    {
                        SimVarName = varNameWithAlias.Trim(),
                        Unit = string.Empty,
                        Value = 0.0,
                        OutputAlias = null,
                        IsClientData = true,
                        ClientAreaId = 0,
                        ClientOffset = -1,
                        ClientSizeOrType = 0,
                        ClientTypeName = "invalid"
                    };
                }
                return clientRef;
            }

            var (varName, parsedUnit, alias) = ParseVarName(varNameWithAlias);
            return new SimVarReference
            {
                SimVarName = varName,
                Unit = parsedUnit ?? unit,
                Value = value,
                OutputAlias = alias
            };
        }

        /// <summary>
        /// True when the name uses the client data prefix "CLIENT:".
        /// </summary>
        public static bool IsClientDataName(string? input)
        {
            return input != null && input.TrimStart().StartsWith("CLIENT:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Parse a client data reference.
        /// Format: CLIENT:AREA_NAME[@AREA_ID]:OFFSET[:TYPE]|OUTPUT_ALIAS
        ///   AREA_ID is optional and only needed for areas not in KnownClientDataAreas.
        ///   TYPE is one of bool,byte,int8,int16,uint16,int32,uint32,int64,uint64,float,double (default float64).
        ///   OFFSET may be hex (0x1B5) or decimal.
        /// Returns null when the reference is malformed.
        /// </summary>
        public static SimVarReference? ParseClientReference(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;

            string rest = input.Trim();
            string? alias = null;

            int pipeIndex = rest.IndexOf('|');
            if (pipeIndex >= 0)
            {
                alias = rest.Substring(pipeIndex + 1).Trim();
                if (alias.Length == 0) alias = null;
                rest = rest.Substring(0, pipeIndex).Trim();
            }

            if (!rest.StartsWith("CLIENT:", StringComparison.OrdinalIgnoreCase)) return null;
            rest = rest.Substring("CLIENT:".Length).Trim();

            // AREA[@AREA_ID]:OFFSET[:TYPE]
            var parts = rest.Split(':');
            if (parts.Length < 2) return null;

            string areaName = parts[0].Trim();
            if (areaName.Length == 0) return null;

            int areaId;
            int atSign = areaName.IndexOf('@');
            if (atSign >= 0)
            {
                // Explicit area ID published by the add-on: CLIENT:AREA@0x4E473331:0x1B5
                string idToken = areaName.Substring(atSign + 1).Trim();
                areaName = areaName.Substring(0, atSign).Trim();
                if (areaName.Length == 0 || !TryParseNumber(idToken, out areaId)) return null;
            }
            else if (!KnownClientDataAreas.TryGetId(areaName, out areaId))
            {
                return null; // unknown area and no explicit ID
            }

            int index = 1;
            if (index >= parts.Length) return null;
            if (!TryParseNumber(parts[index], out int offset)) return null;
            if (offset < 0) return null;

            uint sizeOrType = SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT64;
            string typeName = "float64";

            if (index + 1 < parts.Length)
            {
                string typeToken = parts[index + 1].Trim().ToLowerInvariant();
                switch (typeToken)
                {
                    case "bool":
                    case "byte":
                        // One byte, unsigned: matches C++ bool and unsigned char in add-on SDKs.
                        sizeOrType = SimConnect.SIMCONNECT_CLIENTDATATYPE_INT8;
                        typeName = "byte";
                        break;
                    case "int8":
                        sizeOrType = SimConnect.SIMCONNECT_CLIENTDATATYPE_INT8;
                        typeName = "int8";
                        break;
                    case "uint8":
                        sizeOrType = SimConnect.SIMCONNECT_CLIENTDATATYPE_INT8;
                        typeName = "byte";
                        break;
                    case "int16":
                    case "short":
                        sizeOrType = SimConnect.SIMCONNECT_CLIENTDATATYPE_INT16;
                        typeName = "int16";
                        break;
                    case "uint16":
                    case "ushort":
                    case "word":
                        sizeOrType = SimConnect.SIMCONNECT_CLIENTDATATYPE_INT16;
                        typeName = "uint16";
                        break;
                    case "int32":
                    case "int":
                        sizeOrType = SimConnect.SIMCONNECT_CLIENTDATATYPE_INT32;
                        typeName = "int32";
                        break;
                    case "uint32":
                    case "uint":
                    case "dword":
                        sizeOrType = SimConnect.SIMCONNECT_CLIENTDATATYPE_INT32;
                        typeName = "uint32";
                        break;
                    case "int64":
                    case "long":
                        sizeOrType = SimConnect.SIMCONNECT_CLIENTDATATYPE_INT64;
                        typeName = "int64";
                        break;
                    case "uint64":
                    case "ulong":
                        sizeOrType = SimConnect.SIMCONNECT_CLIENTDATATYPE_INT64;
                        typeName = "uint64";
                        break;
                    case "float":
                    case "float32":
                        sizeOrType = SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT32;
                        typeName = "float32";
                        break;
                    case "double":
                    case "float64":
                        sizeOrType = SimConnect.SIMCONNECT_CLIENTDATATYPE_FLOAT64;
                        typeName = "float64";
                        break;
                    default:
                        return null;
                }
            }

            return new SimVarReference
            {
                SimVarName = areaName,
                Unit = string.Empty,
                Value = 0.0,
                OutputAlias = alias ?? $"{areaName}:{offset}",
                IsClientData = true,
                ClientAreaId = areaId,
                ClientOffset = offset,
                ClientSizeOrType = sizeOrType,
                ClientTypeName = typeName
            };
        }

        private static bool TryParseNumber(string token, out int value)
        {
            token = token.Trim();
            if (token.Length == 0)
            {
                value = 0;
                return false;
            }

            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(token.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
            }

            return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Update an existing SimVarReference to parse and apply unit and alias from SimVarName if present.
        /// Used when receiving requests where the metadata might be embedded in the name.
        /// </summary>
        public static SimVarReference NormalizeReference(SimVarReference reference)
        {
            if (reference.IsClientData) return reference;

            if (IsClientDataName(reference.SimVarName))
            {
                return ParseClientReference(reference.SimVarName)
                    ?? reference with { IsClientData = true, ClientOffset = -1, ClientTypeName = "invalid" };
            }

            var (varName, unit, alias) = ParseVarName(reference.SimVarName);
            
            // If alias or unit was parsed from the name, update the reference
            if (alias != null || unit != null)
            {
                return reference with
                {
                    SimVarName = varName,
                    Unit = unit ?? reference.Unit,
                    OutputAlias = alias ?? reference.OutputAlias
                };
            }
            
            // No metadata in name, return as-is
            return reference;
        }
    }
}
