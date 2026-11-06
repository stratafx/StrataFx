using System.ComponentModel.DataAnnotations;
using StrataFx.Annotations;

namespace StrataFx.Sample.Sales;

// These plain classes are all StrataFx needs: list views, detail forms, lookups,
// navigation and validation are generated from them.

[StrataEntity(NavigationGroup = "Sales", Order = 1, Icon = MudBlazor.Icons.Material.Outlined.People)]
public class Customer
{
    public int Id { get; set; }

    [Required, StringLength(100)]
    public string Name { get; set; } = "";

    [EmailAddress, StringLength(200)]
    public string? Email { get; set; }

    [Phone, StringLength(40)]
    public string? Phone { get; set; }

    [StringLength(80)]
    public string? City { get; set; }

    [Display(Name = "VIP")]
    public bool IsVip { get; set; }

    [Display(Name = "Customer since"), DataType(DataType.Date)]
    public DateTime CreatedOn { get; set; } = DateTime.Today;

    [Multiline, Display(GroupName = "Notes")]
    public string? Notes { get; set; }

    public List<Order> Orders { get; set; } = []; // collections are not shown yet
}

public enum ProductCategory { Hardware, Software, Service, Subscription }

[StrataEntity(NavigationGroup = "Sales", Order = 2, Icon = MudBlazor.Icons.Material.Outlined.Inventory2)]
public class Product
{
    public int Id { get; set; }

    [Required, StringLength(120)]
    public string Name { get; set; } = "";

    [Required, StringLength(30), Display(Name = "SKU")]
    public string Sku { get; set; } = "";

    public ProductCategory Category { get; set; }

    [Range(0, 1_000_000), DisplayFormat(DataFormatString = "{0:N2}")]
    public decimal Price { get; set; }

    [Display(Name = "In stock")]
    public bool InStock { get; set; } = true;
}

public enum OrderStatus { New, Processing, Shipped, Cancelled }

[StrataEntity(NavigationGroup = "Sales", Order = 3, Icon = MudBlazor.Icons.Material.Outlined.ReceiptLong)]
[LookupText(nameof(Number))]
public class Order
{
    public int Id { get; set; }

    [Required, StringLength(20), Display(Name = "Order #")]
    public string Number { get; set; } = "";

    // Non-nullable reference → required. The CustomerId foreign key is hidden automatically.
    public Customer Customer { get; set; } = null!;
    public int CustomerId { get; set; }

    public Product? Product { get; set; }
    public int? ProductId { get; set; }

    [Range(1, 10_000)]
    public int Quantity { get; set; } = 1;

    [DataType(DataType.Date), Display(Name = "Order date")]
    public DateTime OrderDate { get; set; } = DateTime.Today;

    public OrderStatus Status { get; set; }

    [Display(Name = "Shipped on")]
    public DateTime? ShippedOn { get; set; }

    [Range(0, 100_000_000), DisplayFormat(DataFormatString = "{0:N2}")]
    public decimal Total { get; set; }

    [Multiline(3), Display(GroupName = "Notes")]
    public string? Notes { get; set; }
}
