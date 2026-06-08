namespace Test_Wolverin_Marten.Domain;

public enum OrderStatus { Pending, Confirmed }

public sealed class Order
{
    public Guid Id { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public OrderStatus Status { get; set; } = OrderStatus.Pending;
}
