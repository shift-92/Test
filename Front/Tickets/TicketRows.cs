using UniversalTables.Models;
using UniversalTables.Services;

namespace Front.Tickets;

public abstract class TicketRow : ITableModel
{
    public Guid Id { get; set; }
    public string ToDescription() => $"{this} ({Id})";
}

public sealed class ClusterRow : TicketRow
{
    public string Name { get; set; } = "";
    public Guid ItSystemId { get; set; }
    public override string ToString() => Name;
}

public sealed class VmRow : TicketRow
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime Date { get; set; }
    public Guid ItSystemId { get; set; }
    public Guid? UsageId { get; set; }
    [Common.Ticket.Models.MainObject(nameof(Cluster))] public Guid? ClusterId { get; set; }
    public ClusterRow? Cluster { get; set; }
    public override string ToString() => Name;
}

public sealed class AddressRow : TicketRow
{
    public string Address { get; set; } = "";
    public override string ToString() => Address;
}

public sealed class RuleRow : TicketRow
{
    public string Name { get; set; } = "";
    [Common.Ticket.Models.MainObject(nameof(IpAddress))] public Guid IpAddressId { get; set; }
    public AddressRow? IpAddress { get; set; }
    public override string ToString() => Name;
}

public static class TicketTables
{
    public static void Register(ModelRegistry registry)
    {
        registry.Add<VmRow>("vm", "Vms", (_, c) =>
        {
            c.Add(x => x.Name).Title("Имя").Editable().Definition.Required = true;
            c.Add(x => x.Description).Title("Описание").Width(300).Editable();
            c.Add(x => x.Date).Title("Дата").Editable();
            c.Add(x => x.ClusterId).Title("Кластер").Editable();
            c.Add(x => x.UsageId).Title("ID назначения");
        });
        registry.Add<ClusterRow>("cluster", "Clusters", (_, c) =>
        {
            c.Add(x => x.Name).Title("Имя").Editable().Definition.Required = true;
        });
        registry.Add<RuleRow>("networkRule", "NetworkRules", (_, c) =>
        {
            c.Add(x => x.Name).Title("Имя").Editable().Definition.Required = true;
            c.Add(x => x.IpAddressId).Title("IP-адрес").Editable();
        });
        registry.Add<AddressRow>("ipAddress", "IP-адреса", (_, c) => c.Add(x => x.Address));
    }
}