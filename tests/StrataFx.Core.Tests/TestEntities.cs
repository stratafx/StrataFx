using System.ComponentModel.DataAnnotations;
using StrataFx.Annotations;

namespace StrataFx.Core.Tests;

[StrataEntity(NavigationGroup = "Crm")]
public class Company
{
    public int Id { get; set; }

    [Required, StringLength(50)]
    public string Name { get; set; } = "";

    public string? Website { get; set; }

    [Multiline]
    public string? Remarks { get; set; }

    public List<Contact> Contacts { get; set; } = [];
}

public enum ContactKind { Lead, Customer, FormerCustomer }

[StrataEntity(NavigationGroup = "Crm", PluralCaption = "People")]
[LookupText(nameof(FullName))]
public class Contact
{
    public int Id { get; set; }

    [Required]
    public string FullName { get; set; } = "";

    public Company Company { get; set; } = null!;
    public int CompanyId { get; set; }

    public Company? Referrer { get; set; }
    public int? ReferrerId { get; set; }

    public ContactKind Kind { get; set; }

    [Display(Name = "Date of birth", Order = 1), DataType(DataType.Date)]
    public DateTime? BirthDate { get; set; }

    [DisplayFormat(DataFormatString = "{0:N2}")]
    public decimal CreditLimit { get; set; }

    [HideIn(ViewKinds.List)]
    public bool Active { get; set; } = true;

    public string Computed => FullName.ToUpperInvariant();
}

public class NoKeyEntity
{
    public string Name { get; set; } = "";
}
