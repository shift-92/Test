using System.Text.Json;
using Common.Ticket.Models;
using UniversalTables.Models;
using UniversalTables.Services;
using HistoryKey = Common.Ticket.Models.EntityKey;

namespace Front.Tickets;

public sealed record TicketSnapshot(Guid Id, long LastSequence, EntityState[] OriginalRows, EntityState[] CurrentRows, StoredAction[] Actions);

public sealed class TicketSession
{
    public Guid Id { get; }
    public long Sequence { get; private set; }
    public Guid ItSystemId { get; }
    public CompositeTableDataSource Data { get; }
    private readonly ModelRegistry _registry;
    private EditorState _original = new(), _current = new();
    private Dictionary<Type, IReadOnlyList<object>> _rows = [];
    private Dictionary<Type, HashSet<Guid>> _deleted = [];

    public TicketSession(TicketSnapshot snapshot, ModelRegistry registry)
    {
        Id = snapshot.Id;
        ItSystemId = snapshot.OriginalRows.Single(r => r.Key.Type == "itSystem").Key.Id;
        _registry = registry;
        Data = new(new(TableMode.Ticket, Guid.Empty, Id), registry, _ => [], t => _rows[t],
            t => _rows.ContainsKey(t), t => _deleted[t],
            k => new(_current.Entities[new(k.ObjectType, k.Id)].IsDeleted,
                !_original.Entities.ContainsKey(new(k.ObjectType, k.Id))), Difference, _ => true);
        Update(snapshot);
    }

    public void Update(TicketSnapshot snapshot)
    {
        if (snapshot.Id != Id || snapshot.LastSequence < Sequence) return;
        var current = StateJson.FromRows(snapshot.CurrentRows);
        var original = StateJson.FromRows(snapshot.OriginalRows);
        var rows = new Dictionary<Type, IReadOnlyList<object>>();
        var deleted = new Dictionary<Type, HashSet<Guid>>();
        foreach (var model in _registry.All)
        {
            var entities = snapshot.CurrentRows.Where(r => r.Key.Type == model.ObjectType).ToList();
            rows[model.Type] = entities.Select(r =>
            {
                var row = (TicketRow)JsonSerializer.SerializeToElement(r.Properties).Deserialize(model.Type, ChangeJson.Options)!;
                row.Id = r.Key.Id;
                return (object)row;
            }).ToArray();
            deleted[model.Type] = entities.Where(r => r.IsDeleted).Select(r => r.Key.Id).ToHashSet();
        }
        var clusters = rows[typeof(ClusterRow)].Cast<ClusterRow>().ToDictionary(x => x.Id);
        foreach (var vm in rows[typeof(VmRow)].Cast<VmRow>())
            vm.Cluster = vm.ClusterId is Guid id ? clusters.GetValueOrDefault(id) : null;
        var addresses = rows[typeof(AddressRow)].Cast<AddressRow>().ToDictionary(x => x.Id);
        foreach (var rule in rows[typeof(RuleRow)].Cast<RuleRow>())
            rule.IpAddress = addresses.GetValueOrDefault(rule.IpAddressId);

        (_rows, _deleted, _original, _current, Sequence) = (rows, deleted, original, current, snapshot.LastSequence);
        Data.PublishInvalidation(new(_registry.All.Select(x => x.ObjectType).ToHashSet(), Sequence));
    }

    private CellDifference Difference(CellKey cell)
    {
        var key = new HistoryKey(cell.Entity.ObjectType, cell.Entity.Id);
        if (!_original.Entities.TryGetValue(key, out var original) ||
            !original.Properties.TryGetValue(cell.ColumnId, out var before) ||
            !_current.Entities[key].Properties.TryGetValue(cell.ColumnId, out var after) ||
            JsonElement.DeepEquals(before, after)) return CellDifference.None;
        return new(true, before.ToString(), after.ToString());
    }
}