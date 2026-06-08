namespace Test_Wolverin_Marten.Domain;

public sealed class InventorySnapshot
{
    public string Id { get; set; } = string.Empty; // SKU as document id
    public int Available { get; set; }
    public int Reserved { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
}
