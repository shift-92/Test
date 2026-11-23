using UniversalTables.Models;
namespace UniversalTables.Services;

// Application supplies already-materialized session objects. This adapter never mutates cache objects.
public sealed class CompositeTableDataSource : ITableDataSource
{
    private readonly TableModelRegistry _registry;
    private readonly Func<Type,IReadOnlyList<object>> _cache;
    private readonly Func<Type,IReadOnlyList<object>> _session;
    private readonly Func<Type,bool> _tracked;
    private readonly Func<Type,IReadOnlySet<Guid>> _deleted;
    private readonly Func<TableKey,RowState> _state;
    private readonly Func<CellKey,CellDifference> _difference;
    private readonly Func<TableKey,bool> _canEdit;
    private readonly Func<TableKey,object?>? _originalRow;
    public TableContext Context {get;}
    public event Action<DataInvalidation>? Invalidated;
    public CompositeTableDataSource(TableContext context,TableModelRegistry registry,
        Func<Type,IReadOnlyList<object>> cache,Func<Type,IReadOnlyList<object>> session,
        Func<Type,bool> tracked,Func<Type,IReadOnlySet<Guid>> deleted,
        Func<TableKey,RowState> state,Func<CellKey,CellDifference> difference,Func<TableKey,bool> canEdit,
        Func<TableKey,object?>? originalRow=null)
    {
        Context=context; _registry=registry; _cache=cache; _session=session; _tracked=tracked;
        _deleted=deleted; _state=state; _difference=difference; _canEdit=canEdit;
        _originalRow=originalRow;
    }
    public IReadOnlyList<object> Rows(Type registeredType)
    {
        _registry.Get(registeredType);
        return Context.Mode==TableMode.Ticket && _tracked(registeredType) ? _session(registeredType) : _cache(registeredType);
    }
    public IReadOnlyList<ReferenceOption> ReferenceOptions(Type registeredType)
    {
        _registry.Get(registeredType);
        var result=new Dictionary<Guid,object>();
        foreach(var item in _cache(registeredType)) result[((ITableModel)item).Id]=item;
        if(Context.Mode==TableMode.Ticket && _tracked(registeredType))
        {
            foreach(var item in _session(registeredType)) result[((ITableModel)item).Id]=item;
            foreach(var id in _deleted(registeredType)) result.Remove(id);
        }
        return result.Select(pair=>new ReferenceOption(pair.Key,pair.Value.ToString() ?? pair.Key.ToString())
            { Description=((ITableModel)pair.Value).ToDescription() }).ToArray();
    }
    public ReferenceOption? ResolveReference(Type type,Guid id)
    {
        _registry.Get(type);
        object? item=null;
        var deleted=false;
        if(Context.Mode==TableMode.Ticket && _tracked(type))
        {
            deleted=_deleted(type).Contains(id);
            item=_session(type).FirstOrDefault(x=>((ITableModel)x).Id==id);
            if(deleted) return new(id,(item?.ToString() ?? id.ToString())+" (удалено)",false)
                { Description=item is ITableModel model ? model.ToDescription()+" (удалено)" : null };
        }
        item ??= _cache(type).FirstOrDefault(x=>((ITableModel)x).Id==id);
        return item is null ? null : new(id,item.ToString() ?? id.ToString())
            { Description=((ITableModel)item).ToDescription() };
    }
    public RowState GetRowState(TableKey key)=>Context.Mode==TableMode.Ticket ? _state(key) : new(false,false);
    public CellDifference GetDifference(CellKey key)=>Context.Mode==TableMode.Ticket ? _difference(key) : CellDifference.None;
    public object? GetOriginalRow(TableKey key)=>Context.Mode==TableMode.Ticket ? _originalRow?.Invoke(key) : null;
    public bool CanEdit(TableKey key)
    {
        if(Context.Mode==TableMode.Ticket)
        {
            var type=_registry.Get(key.ObjectType).Type;
            if(!_tracked(type) || _deleted(type).Contains(key.Id) || !_session(type).Any(x=>((ITableModel)x).Id==key.Id)) return false;
        }
        return _canEdit(key);
    }
    public bool CanRestore(TableKey key)
    {
        var type=_registry.Get(key.ObjectType).Type;
        return Context.Mode==TableMode.Ticket && _tracked(type) && _deleted(type).Contains(key.Id) && _canEdit(key);
    }
    public void PublishInvalidation(DataInvalidation change)=>Invalidated?.Invoke(change);
}