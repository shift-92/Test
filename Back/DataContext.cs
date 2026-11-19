using Common.Ticket.Models;
using Microsoft.EntityFrameworkCore;

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
