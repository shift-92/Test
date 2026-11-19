using Common.Ticket.Models;
using System.ComponentModel.DataAnnotations;

namespace Common.Ticket.Domain;

[HistoryType("itSystem"), Display(Name = "ИТ-система")]
public sealed class DbItSystem : ITicketModel
{
    public Guid Id { get; set; }
    [Display(Name = "Имя")] public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<DbVm> Vms { get; set; } = [];
    public List<DbCluster> Clusters { get; set; } = [];
}

[HistoryType("cluster"), Display(Name = "Кластер")]
public sealed class DbCluster : ITicketModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [MainObject(nameof(ItSystem)), HistoryDeleteBehavior(ReferenceDeleteBehavior.Cascade)]
    public Guid ItSystemId { get; set; }
    public DbItSystem? ItSystem { get; set; }
    public List<DbVm> Vms { get; set; } = [];
}

[HistoryType("vm"), Display(Name = "Виртуальная машина")]
public sealed class DbVm : ITicketModel
{
    public Guid Id { get; set; }
    [Display(Name = "Имя")] public string Name { get; set; } = string.Empty;
    [Display(Name = "Описание")] public string? Description { get; set; }
    public DateTime Date { get; set; }
    [MainObject(nameof(ItSystem)), HistoryDeleteBehavior(ReferenceDeleteBehavior.Cascade)]
    public Guid ItSystemId { get; set; }
    public DbItSystem? ItSystem { get; set; }
    [MainObject(nameof(Cluster)), HistoryDeleteBehavior(ReferenceDeleteBehavior.SetNull)]
    public Guid? ClusterId { get; set; }
    public DbCluster? Cluster { get; set; }
    [MainObject(nameof(Usage)), ExternalReference]
    public Guid? UsageId { get; set; }
    public DbUsage? Usage { get; set; }
}

public sealed class DbUsage : ITicketModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

[HistoryType("vmDisk"), Display(Name = "Диск VM")]
public sealed class DbVmDisk : ITicketModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal SizeGb { get; set; }
    [MainObject(nameof(Vm)), HistoryDeleteBehavior(ReferenceDeleteBehavior.Cascade)]
    public Guid VmId { get; set; }
    public DbVm? Vm { get; set; }
}

[HistoryType("ipAddress"), Display(Name = "IP-адрес")]
public sealed class DbIpAddress : ITicketModel
{
    public Guid Id { get; set; }
    public string Address { get; set; } = string.Empty;
    [MainObject(nameof(Vm)), HistoryDeleteBehavior(ReferenceDeleteBehavior.Cascade)]
    public Guid VmId { get; set; }
    public DbVm? Vm { get; set; }
}

[HistoryType("networkRule"), Display(Name = "Сетевое правило")]
public sealed class DbNetworkRule : ITicketModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [MainObject(nameof(IpAddress), AllowOutsideSnapshot = true), HistoryDeleteBehavior(ReferenceDeleteBehavior.Cascade)]
    public Guid IpAddressId { get; set; }
    [MainObject(nameof(TargetAddress), AllowOutsideSnapshot = true), HistoryDeleteBehavior(ReferenceDeleteBehavior.Cascade)]
    public Guid TargetAddressId { get; set; }
    public DbIpAddress? IpAddress { get; set; }
    public DbIpAddress? TargetAddress { get; set; }
}