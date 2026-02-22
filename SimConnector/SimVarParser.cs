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
        /// Update an existing SimVarReference to parse and apply unit and alias from SimVarName if present.
        /// Used when receiving requests where the metadata might be embedded in the name.
        /// </summary>
        public static SimVarReference NormalizeReference(SimVarReference reference)
        {
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
