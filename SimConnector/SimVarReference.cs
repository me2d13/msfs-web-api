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
    }
}
