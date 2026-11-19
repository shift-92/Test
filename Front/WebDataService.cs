using Common.Ticket.Domain;

namespace Front;

public class WebDataService
{
    public IReadOnlyList<T> GetObjects<T>() where T : class, Common.Ticket.Models.ITicketModel => Table<T>.Read(this);

    // Связываем тип с его таблицей один раз; замена списка не меняет этот accessor.
    private static class Table<T> where T : class, Common.Ticket.Models.ITicketModel
    {
        internal static readonly Func<WebDataService, List<T>> Read = typeof(WebDataService).GetProperties()
            .Single(p => p.PropertyType == typeof(List<T>)).GetMethod!.CreateDelegate<Func<WebDataService, List<T>>>();
    }

    public List<DbItSystem> ItSystems { get; set; } = [];
    public List<DbCluster> Clusters { get; set; } = [];
    public List<DbVm> Vms { get; set; } = [];
    public List<DbUsage> Usages { get; set; } = [new DbUsage { Id = Guid.Parse("30000000-0000-0000-0000-000000000001"), Name = "1" }, new DbUsage { Id = Guid.Parse("30000000-0000-0000-0000-000000000002"), Name = "2" }];
    public List<DbVmDisk> VmDisks { get; set; } = [];
    public List<DbIpAddress> IpAddresses { get; set; } = [];
    public List<DbNetworkRule> NetworkRules { get; set; } = [];
}