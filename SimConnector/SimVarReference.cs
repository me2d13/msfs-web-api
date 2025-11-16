namespace SimConnector
{
    /// <summary>
    /// Represents a SimVar reference for SimConnect operations and API requests.
    /// </summary>
    public record SimVarReference
    {
        /// <summary>
        /// The name of the SimVar.
        /// </summary>
        public string SimVarName { get; init; } = string.Empty;
        /// <summary>
        /// The unit of the SimVar (e.g., "feet", "knots").
        /// </summary>
        public string Unit { get; init; } = string.Empty;
        /// <summary>
        /// The value of the SimVar (used for set operations or responses).
        /// </summary>
        public double Value { get; init; } = 0.0;
        /// <summary>
        /// Optional output alias for JSON responses. If not set, SimVarName is used.
        /// Supports pipe delimiter format: "ACTUAL_VAR_NAME|OUTPUT_ALIAS"
        /// </summary>
        public string? OutputAlias { get; init; }
        /// <summary>
        /// Gets the name to use in output (alias if set, otherwise SimVarName).
        /// </summary>
        public string GetOutputName() => OutputAlias ?? SimVarName;
    }
}
