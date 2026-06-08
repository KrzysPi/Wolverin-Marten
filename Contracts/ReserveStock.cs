namespace Test_Wolverin_Marten.Contracts;

public sealed record ReserveStock(Guid OrderId, string Sku, int Quantity);
