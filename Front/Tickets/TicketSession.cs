using System.Text.Json;
using System.Reflection;
using Common.Ticket.Domain;
using Common.Ticket.Models;
using Front.Components.Shared;
using UniversalTables.Models;
using UniversalTables.Services;
using HistoryKey = Common.Ticket.Models.EntityKey;

namespace Front.Tickets;

public sealed record TicketSnapshot(Guid Id, long LastSequence, EntityState[] OriginalRows, EntityState[] CurrentRows, StoredAction[] Actions,
    EnumTicketStatus CurrentStatus = EnumTicketStatus.Create, int StatusRevision = 0);

public sealed class TicketSession
{
    public Guid Id { get; }
    public long Sequence { get; private set; }
    public EnumTicketStatus Status { get; private set; }
    private int _statusRevision = -1;
    public Guid ItSystemId { get; }
    public CompositeTableDataSource Data { get; }
    private readonly ModelRegistry _registry;
    private EditorState _original = new(), _current = new();
    internal EditorState Original => _original;
    internal EditorState Current => _current;
    internal IReadOnlyList<StoredAction> Actions { get; private set; } = [];
    public IReadOnlyList<UiLog> Log { get; private set; } = [];
    public IReadOnlyList<UiWork> Work { get; private set; } = [];
    private Dictionary<CellKey, CellDifference> _collectionChanges = [];
    private readonly ChangesViewService _views;
    private readonly ILookup<string, (string Child, string Parent, string Column, string ForeignKey)> _relations;
    private Dictionary<Type, IReadOnlyList<object>> _rows = [];
    private Dictionary<HistoryKey, object>? _originalRows;
    private Dictionary<Type, HashSet<Guid>> _deleted = [];

    public TicketSession(TicketSnapshot snapshot, ModelRegistry registry, ChangesViewService views, WebDataService cache)
    {
        Id = snapshot.Id;
        ItSystemId = snapshot.OriginalRows.Single(r => r.Key.Type == "itSystem").Key.Id;
        _registry = registry;
        _views = views;
        _relations = registry.All.SelectMany(parent => parent.Type.GetProperties()
            .Select(p => (Property: p, Link: p.GetCustomAttribute<PredictFieldAttribute>()))
            .Where(p => p.Link is not null)
            .Select(p => (Child: registry.Get(p.Property.PropertyType.GetGenericArguments().Single()).ObjectType,
                Parent: parent.ObjectType, Column: p.Property.Name, ForeignKey: p.Link!.ForeignKey))).ToLookup(r => r.Child);
        // Только справочные строки. Для внутренних FK (например, ClusterId) кэш не подмешиваем.
        Dictionary<Type, IReadOnlyList<object>> references = new()
        {
            [typeof(UsageRow)] = cache.GetObjects<DbUsage>().Select(u => new UsageRow { Id = u.Id, Name = u.Name }).ToArray(),
            [typeof(AddressRow)] = cache.GetObjects<DbIpAddress>().Select(a => new AddressRow { Id = a.Id, Address = a.Address, VmId = a.VmId }).ToArray()
        };
        Data = new(new(TableMode.Ticket, Guid.Empty, Id), registry, t => references.GetValueOrDefault(t) ?? [], t => _rows[t], t => _rows.ContainsKey(t), t => _deleted[t], k => new(_current.Entities[new(k.ObjectType, k.Id)].IsDeleted, !_original.Entities.ContainsKey(new(k.ObjectType, k.Id))), Difference, _ => true, k => OriginalRow(new(k.ObjectType, k.Id)));
        Update(snapshot);
    }

    public void Update(TicketSnapshot snapshot)
    {
        if (snapshot.Id != Id || snapshot.LastSequence < Sequence || snapshot.StatusRevision < _statusRevision ||
            (snapshot.LastSequence == Sequence && snapshot.StatusRevision == _statusRevision)) return;
        var current = StateJson.FromRows(snapshot.CurrentRows);
        var original = StateJson.FromRows(snapshot.OriginalRows);
        var rows = Materialize(snapshot.CurrentRows);
        var deleted = _registry.All.ToDictionary(m => m.Type, m => snapshot.CurrentRows
            .Where(r => r.Key.Type == m.ObjectType && r.IsDeleted).Select(r => r.Key.Id).ToHashSet());

        var work = _views.BuildWork(original, current);
        // История заявки дописывается. При сохранении не воспроизводим повторно весь журнал.
        var extendsHistory = Actions.Count <= snapshot.Actions.Length && Actions.Select(a => a.ActionId)
            .SequenceEqual(snapshot.Actions.Take(Actions.Count).Select(a => a.ActionId));
        var log = extendsHistory && Actions.Count > 0
            ? Log.Concat(_views.BuildLog(_current, snapshot.Actions[Actions.Count..])).ToList()
            : _views.BuildLog(original, snapshot.Actions);
        var collections = CollectionChanges(original, current, work);
        (Work, Log, Actions, _collectionChanges) = (work, log, snapshot.Actions, collections);
        (_rows, _deleted, _original, _current, Sequence) = (rows, deleted, original, current, snapshot.LastSequence);
        _originalRows = null;
        (Status, _statusRevision) = (snapshot.CurrentStatus, snapshot.StatusRevision);
        Data.PublishInvalidation(new(_registry.All.Select(x => x.ObjectType).ToHashSet(), Sequence));
    }

    private Dictionary<Type, IReadOnlyList<object>> Materialize(IEnumerable<EntityState> states)
    {
        var byType = states.ToLookup(r => r.Key.Type);
        var rows = new Dictionary<Type, IReadOnlyList<object>>();
        foreach (var model in _registry.All)
            rows[model.Type] = [.. byType[model.ObjectType].Select(r =>
            {
                var row = (TicketRow)JsonSerializer.SerializeToElement(r.Properties).Deserialize(model.Type, ChangeJson.Options)!;
                row.Id = r.Key.Id;
                return (object)row;
            })];
        RowRelations.Rebuild(rows);
        return rows;
    }

    private object? OriginalRow(HistoryKey key)
    {
        _originalRows ??= Materialize(_original.Entities.Values).SelectMany(group => group.Value.Select(row =>
            KeyValuePair.Create(new HistoryKey(_registry.Get(group.Key).ObjectType, ((TicketRow)row).Id), row))).ToDictionary();
        return _originalRows.GetValueOrDefault(key);
    }

    private CellDifference Difference(CellKey cell)
    {
        if (_collectionChanges.TryGetValue(cell, out var collection)) return collection;
        var key = new HistoryKey(cell.Entity.ObjectType, cell.Entity.Id);
        if (!_original.Entities.TryGetValue(key, out var original) || !original.Properties.TryGetValue(cell.ColumnId, out var before) || !_current.Entities[key].Properties.TryGetValue(cell.ColumnId, out var after) || JsonElement.DeepEquals(before, after)) return CellDifference.None;
        return new(true, before.ToString(), after.ToString());
    }

    // Индекс только итоговых отличий: удаление свежедобавленной строки или полная отмена
    // правки остаются в журнале, но не должны подсвечивать коллекцию.
    private Dictionary<CellKey, CellDifference> CollectionChanges(EditorState original, EditorState current, IReadOnlyList<UiWork> work)
    {
        var counts = new Dictionary<CellKey, (int Added, int Changed, int Removed)>();
        foreach (var item in work)
        foreach (var relation in _relations[item.Entity.Type])
        {
            Guid? Parent(EditorState state) => state.Entities.TryGetValue(item.Entity, out var row) && !row.IsDeleted &&
                row.Properties[relation.ForeignKey] is { ValueKind: JsonValueKind.String } fk ? fk.GetGuid() : null;
            var before = Parent(original);
            var after = Parent(current);
            foreach (var id in new[] { before, after }.OfType<Guid>().Distinct())
            {
                var cell = new CellKey(new(relation.Parent, id), relation.Column);
                var count = counts.GetValueOrDefault(cell);
                if (before == after) count.Changed++;
                else if (id == before) count.Removed++;
                else count.Added++;
                counts[cell] = count;
            }
        }
        return counts.ToDictionary(x => x.Key, x => new CellDifference(true, "Исходная коллекция",
            $"Добавлено: {x.Value.Added}, изменено: {x.Value.Changed}, удалено из коллекции: {x.Value.Removed}"));
    }
}