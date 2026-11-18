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

        /// --- Client data ("CLIENT:" references) ---
        /// <summary>True when this reference points into a SimConnect client data area.</summary>
        public bool IsClientData { get; init; }
        /// <summary>Numeric ID of the client data area, as published by the add-on that created it.</summary>
        public int ClientAreaId { get; init; }
        /// <summary>Byte offset of the field inside the client data area.</summary>
        public int ClientOffset { get; init; }
        /// <summary>
        /// dwSizeOrType for AddToClientDataDefinition: a byte count or one of the
        /// SIMCONNECT_CLIENTDATATYPE_* constants.
        /// </summary>
        public uint ClientSizeOrType { get; init; }
        /// <summary>Declared type name, used in logs and diagnostics.</summary>
        public string ClientTypeName { get; init; } = "float64";

        /// <summary>
        /// Gets the name to use in output (alias if set, otherwise SimVarName).
        /// </summary>
        public string GetOutputName() => OutputAlias ?? SimVarName;

        /// <summary>Stable identity of a client data field (area + offset + type).</summary>
        public string ClientKey() => $"{SimVarName}:{ClientAreaId}:{ClientOffset}:{ClientSizeOrType}";
    }
}
