namespace SimConnector
{
    /// <summary>
    /// Helper for parsing SimVar names with optional output aliases.
  /// Format: "VAR_NAME|OUTPUT_ALIAS" or just "VAR_NAME"
    /// </summary>
    public static class SimVarParser
  {
        /// <summary>
        /// Parse a SimVar string that may contain a pipe-delimited alias.
     /// Returns (actualVarName, outputAlias).
        /// If no pipe, outputAlias will be null.
        /// </summary>
      public static (string varName, string? alias) ParseVarName(string input)
    {
   if (string.IsNullOrWhiteSpace(input))
     return (string.Empty, null);

            var parts = input.Split('|', 2);
            if (parts.Length == 2)
            {
      // Format: "VAR_NAME|ALIAS"
     var varName = parts[0].Trim();
      var alias = parts[1].Trim();
            return (varName, string.IsNullOrWhiteSpace(alias) ? null : alias);
            }
            
   // No pipe, use as-is
            return (input.Trim(), null);
        }

        /// <summary>
      /// Create a SimVarReference from a string that may contain an alias,
        /// preserving existing unit and value if provided.
        /// </summary>
     public static SimVarReference CreateReference(string varNameWithAlias, string unit = "", double value = 0.0)
      {
            var (varName, alias) = ParseVarName(varNameWithAlias);
   return new SimVarReference
   {
         SimVarName = varName,
       Unit = unit,
         Value = value,
      OutputAlias = alias
       };
        }

   /// <summary>
        /// Update an existing SimVarReference to parse and apply alias from SimVarName if present.
        /// Used when receiving requests where the alias might be embedded in the name.
        /// </summary>
        public static SimVarReference NormalizeReference(SimVarReference reference)
  {
        var (varName, alias) = ParseVarName(reference.SimVarName);
            
            // If alias was parsed from the name, update the reference
            if (alias != null)
     {
                return reference with
    {
      SimVarName = varName,
        OutputAlias = alias
      };
            }
            
            // No alias in name, return as-is
      return reference;
   }
    }
}
