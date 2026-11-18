using Common.Ticket.Models;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace Back;

public class DataContext(DbContextOptions<DataContext> options) : DbContext(options)
{
    public DbSet<DbItSystem> ItSystems => Set<DbItSystem>();
    public DbSet<DbCluster> Clusters => Set<DbCluster>();
    public DbSet<DbVm> Vms => Set<DbVm>();
    public DbSet<DbUsage> Usages => Set<DbUsage>();
    public DbSet<DbVmDisk> VmDisks => Set<DbVmDisk>();
    public DbSet<DbIpAddress> IpAddresses => Set<DbIpAddress>();
    public DbSet<DbNetworkRule> NetworkRules => Set<DbNetworkRule>();
    public DbSet<DbTicket> Tickets => Set<DbTicket>();
    public DbSet<DbTicketAction> TicketActions => Set<DbTicketAction>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DbTicket>(b =>
        {
            b.ToTable("tickets", "dbo"); b.HasKey(x => x.Id);
            b.Property(x => x.OriginalJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.CurrentJson).HasColumnType("nvarchar(max)");
        });
        model.Entity<DbTicketAction>(b =>
        {
            b.ToTable("ticket_actions", "dbo");
            b.HasOne<DbTicket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Restrict);
        });
        model.Entity<DbTicketStatus>(b =>
        {
            b.ToTable("ticket_statuses", "dbo");
            b.HasOne<DbTicket>().WithMany().HasForeignKey(x => x.TicketId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}

public enum EnumTicketStatus
{
    Create,
    Project,
    Open,
    InWork,
    Complited,
    NotComplited
}

public class DbTicket
{
    public Guid Id { get; set; }
    public EnumTicketStatus CurrentStatus { get; set; } 
    public long LastSequence { get; set; }
    public string OriginalJson { get; set; } = string.Empty;
    public string CurrentJson { get; set; } = string.Empty;
    public ICollection<DbTicketAction> Actions { get; set; } = [];
    public ICollection<DbTicketStatus> Statuses { get; set; } = [];
}

public class DbTicketAction
{
    public Guid Id { get; set; }
    public long Sequence { get; set; }
    public Guid TicketId { get; set; }
    public Guid UserId { get; set; }
    public DateTime DateTimeUtc { get; set; }
    public string RequestJson { get; set; } = string.Empty;
    public string ActionJson { get; set; } = string.Empty;
    public virtual DbTicket Ticket { get; set; } = null!;
}

public class DbTicketStatus
{
    public Guid Id { get; set; }
    public Guid TicketId { get; set; }
    public DateTime DateTimeUtc { get; set; }
    public EnumTicketStatus Status { get; set; }
    public Guid UserId { get; set; }
    public string RequestJson { get; set; } = string.Empty;
    public string ActionJson { get; set; } = string.Empty;
    public virtual DbTicket Ticket { get; set; } = null!;
}

[HistoryType("itSystem")]
[Display(Name = "ИТ-система")]
public sealed class DbItSystem : ITicketModel
{
    public Guid Id { get; set; }
    [Display(Name = "Имя")] public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public List<DbVm> Vms { get; set; } = [];
    public List<DbCluster> Clusters { get; set; } = [];
}

[HistoryType("cluster")]
[Display(Name = "Кластер")]
public sealed class DbCluster : ITicketModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [MainObject(nameof(ItSystem)), HistoryDeleteBehavior(ReferenceDeleteBehavior.Cascade)]
    public Guid ItSystemId { get; set; }
    public DbItSystem? ItSystem { get; set; }
    public List<DbVm> Vms { get; set; } = [];
}

[HistoryType("vm")]
[Display(Name = "Виртуальная машина")]
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

[HistoryType("vmDisk")]
[Display(Name = "Диск VM")]
public sealed class DbVmDisk : ITicketModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal SizeGb { get; set; }
    [MainObject(nameof(Vm)), HistoryDeleteBehavior(ReferenceDeleteBehavior.Cascade)]
    public Guid VmId { get; set; }
    public DbVm? Vm { get; set; }
}

[HistoryType("ipAddress")]
public sealed class DbIpAddress : ITicketModel
{
    public Guid Id { get; set; }
    public string Address { get; set; } = string.Empty;
    [MainObject(nameof(Vm)), HistoryDeleteBehavior(ReferenceDeleteBehavior.Cascade)]
    public Guid VmId { get; set; }
    public DbVm? Vm { get; set; }
}

[HistoryType("networkRule")]
public sealed class DbNetworkRule : ITicketModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    [MainObject(nameof(IpAddress)), HistoryDeleteBehavior(ReferenceDeleteBehavior.Cascade)]
    public Guid IpAddressId { get; set; }
    public DbIpAddress? IpAddress { get; set; }
}