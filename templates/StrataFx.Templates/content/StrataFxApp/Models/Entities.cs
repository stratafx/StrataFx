using System.ComponentModel.DataAnnotations;
using MudBlazor;
using StrataFx.Annotations;

namespace StrataFxApp.Models;

// Plain C# classes are all StrataFx needs: list views, detail forms, lookups, navigation and validation
// are generated from them. Change captions and visibility in model.json without recompiling.

public enum Industry { Technology, Manufacturing, Retail, Services, Other }

[StrataEntity(NavigationGroup = "Contacts", Order = 1, Icon = Icons.Material.Outlined.Business)]
public class Company
{
    public int Id { get; set; }

    [Required, StringLength(120)]
    public string Name { get; set; } = ""; // unique (see AppDbContext): a duplicate is reported next to the field

    [Url, StringLength(200)]
    public string? Website { get; set; }

    public Industry Industry { get; set; }

    [Display(Name = "Active")]
    public bool IsActive { get; set; } = true;

    [Multiline, Display(GroupName = "Notes")]
    public string? Notes { get; set; }

    // Related: the company's contacts are shown in its form, each a separate record with its own page.
    public List<Contact> Contacts { get; set; } = [];
}

[StrataEntity(NavigationGroup = "Contacts", Order = 2, Icon = Icons.Material.Outlined.People)]
public class Contact
{
    public int Id { get; set; }

    [Required, StringLength(100)]
    public string Name { get; set; } = "";

    [EmailAddress, StringLength(200)]
    public string? Email { get; set; }

    [Phone, StringLength(40)]
    public string? Phone { get; set; }

    // Non-nullable reference: required, edited with a lookup. The CompanyId foreign key is hidden automatically.
    public Company Company { get; set; } = null!;
    public int CompanyId { get; set; }

    [Display(Name = "Last contacted"), DataType(DataType.Date)]
    public DateTime? LastContactedOn { get; set; }
}
