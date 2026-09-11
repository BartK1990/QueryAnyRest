namespace QueryAnyRest.Models;

/// <summary>A header, query parameter or form field that can be parked without deleting it.</summary>
public sealed class KeyValueItem
{
    public bool Enabled { get; set; } = true;
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
