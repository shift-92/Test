using UniversalTables.Models;
using UniversalTables.Services;

namespace Front.Tickets;

internal static class SystemTableData
{
    public static CompositeTableDataSource Create(WebDataService cache, Guid systemId, TableModelRegistry registry)
    {
        var vms = cache.Vms.Where(v => v.ItSystemId == systemId).Select(v => new VmRow
        {
            Id = v.Id, Name = v.Name, Description = v.Description, Date = v.Date,
            ItSystemId = v.ItSystemId, ClusterId = v.ClusterId, UsageId = v.UsageId
        }).ToArray();
        var vmIds = vms.Select(v => v.Id).ToHashSet();
        var usageIds = vms.Select(v => v.UsageId).ToHashSet();
        var addresses = cache.IpAddresses.Where(a => vmIds.Contains(a.VmId))
            .Select(a => new AddressRow { Id = a.Id, Address = a.Address, VmId = a.VmId }).ToArray();
        var addressIds = addresses.Select(a => a.Id).ToHashSet();
        Dictionary<Type, IReadOnlyList<object>> rows = new()
        {
            [typeof(ItSystemRow)] = cache.ItSystems.Where(s => s.Id == systemId)
                .Select(s => new ItSystemRow { Id = s.Id, Name = s.Name, Description = s.Description }).ToArray(),
            [typeof(ClusterRow)] = cache.Clusters.Where(c => c.ItSystemId == systemId)
                .Select(c => new ClusterRow { Id = c.Id, Name = c.Name, ItSystemId = c.ItSystemId }).ToArray(),
            [typeof(VmRow)] = vms,
            [typeof(UsageRow)] = cache.Usages.Where(u => usageIds.Contains(u.Id))
                .Select(u => new UsageRow { Id = u.Id, Name = u.Name }).ToArray(),
            [typeof(DiskRow)] = cache.VmDisks.Where(d => vmIds.Contains(d.VmId))
                .Select(d => new DiskRow { Id = d.Id, Name = d.Name, SizeGb = d.SizeGb, VmId = d.VmId }).ToArray(),
            [typeof(AddressRow)] = addresses,
            [typeof(RuleRow)] = cache.NetworkRules.Where(r => addressIds.Contains(r.IpAddressId) || addressIds.Contains(r.TargetAddressId))
                .Select(r => new RuleRow { Id = r.Id, Name = r.Name, IpAddressId = r.IpAddressId, TargetAddressId = r.TargetAddressId }).ToArray()
        };
        RowRelations.Rebuild(rows);
        return new(new(TableMode.Live, Guid.Empty), registry, type => rows[type], session: _ => [], tracked: _ => false,
            deleted: _ => new HashSet<Guid>(), state: _ => new(false, false), difference: _ => CellDifference.None, canEdit: _ => false);
    }
}