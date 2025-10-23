namespace SimConnector
{
    public record EventReference
    {
        public string Name { get; init; } = string.Empty;
        public uint Value { get; init; } = 0;
    }
}
