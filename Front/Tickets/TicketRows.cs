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
    [PredictField(nameof(VmRow.ClusterId))] public List<VmRow> Vms { get; } = [];
    public override string ToString() => Name;
}

public sealed class UsageRow : TicketRow
{
    public string Name { get; set; } = "";
    public override string ToString() => Name;
}

public sealed class VmRow : TicketRow
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime Date { get; set; }
    public Guid ItSystemId { get; set; }
    [Common.Ticket.Models.MainObject(nameof(Usage))] public Guid? UsageId { get; set; }
    public UsageRow? Usage { get; set; }
    [Common.Ticket.Models.MainObject(nameof(Cluster))] public Guid? ClusterId { get; set; }
    public ClusterRow? Cluster { get; set; }
    [PredictField(nameof(DiskRow.VmId))] public List<DiskRow> Disks { get; } = [];
    [PredictField(nameof(AddressRow.VmId))] public List<AddressRow> IpAddresses { get; } = [];
    public override string ToString() => Name;
}

public sealed class DiskRow : TicketRow
{
    public string Name { get; set; } = "";
    public decimal SizeGb { get; set; }
    public Guid VmId { get; set; }
    public override string ToString() => Name;
}

public sealed class AddressRow : TicketRow
{
    public string Address { get; set; } = "";
    public Guid VmId { get; set; }
    [PredictField(nameof(RuleRow.IpAddressId))] public List<RuleRow> NetworkRules { get; } = [];
    public override string ToString() => Address;
}

public sealed class RuleRow : TicketRow
{
    public string Name { get; set; } = "";
    [Common.Ticket.Models.MainObject(nameof(IpAddress))] public Guid IpAddressId { get; set; }
    public AddressRow? IpAddress { get; set; }
    [Common.Ticket.Models.MainObject(nameof(TargetAddress))] public Guid TargetAddressId { get; set; }
    public AddressRow? TargetAddress { get; set; }
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
            c.Add(x => x.UsageId).Title("Назначение").Editable();
            c.Add(x => x.Disks).Title("Диски").Definition.FormVisible = false;
            c.Add(x => x.IpAddresses).Title("IP-адреса").Definition.FormVisible = false;
        });
        registry.Add<ClusterRow>("cluster", "Clusters", (_, c) =>
        {
            c.Add(x => x.Name).Title("Имя").Editable().Definition.Required = true;
            c.Add(x => x.Vms).Title("Виртуальные машины").Definition.FormVisible = false;
        });
        registry.Add<RuleRow>("networkRule", "NetworkRules", (_, c) =>
        {
            c.Add(x => x.Name).Title("Имя").Editable().Definition.Required = true;
            c.Add(x => x.IpAddressId).Title("IP-адрес источника").Editable();
            c.Add(x => x.TargetAddressId).Title("IP-адрес назначения").Editable();
        });
        registry.Add<AddressRow>("ipAddress", "IP-адреса", (_, c) =>
        {
            c.Add(x => x.Address).Title("IP-адрес").Editable().Definition.Required = true;
            c.Add(x => x.NetworkRules).Title("Сетевые правила").Definition.FormVisible = false;
        });
        registry.Add<DiskRow>("vmDisk", "Диски", (_, c) =>
        {
            c.Add(x => x.Name).Title("Имя").Editable().Definition.Required = true;
            c.Add(x => x.SizeGb).Title("Размер, ГБ").Editable();
        });
        registry.Add<UsageRow>("usage", "Назначения", (_, c) => { c.Add(x => x.Name).Title("Название"); });
    }
}