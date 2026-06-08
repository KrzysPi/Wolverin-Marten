namespace Test_Wolverin_Marten.Contracts;

public sealed record CreateOrder(
	Guid OrderId,
	string CustomerName,
	decimal TotalAmount,
	string Sku,
	int Quantity);
