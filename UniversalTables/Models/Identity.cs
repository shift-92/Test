namespace UniversalTables.Models;

public enum TableMode { Live, Ticket }
public readonly record struct TableKey(string ObjectType, Guid Id);
public readonly record struct CellKey(TableKey Entity, string ColumnId);
public sealed record TableContext(TableMode Mode, Guid UserId, Guid? TicketId = null)
{
    public string SettingsType(string objectType) => Mode == TableMode.Ticket ? "Ticket" + objectType : objectType;
    public string QueryStorageKey(string objectType) => "UniversalTables:query:v1:" + UserId.ToString("D") + ":" + Mode + ":" + Uri.EscapeDataString(objectType);
    public string OperationScope(TableKey entity) => Mode == TableMode.Ticket ? "ticket:" + (TicketId ?? throw new InvalidOperationException("Необходимо указать TicketId.")) : "live:" + entity.ObjectType + ":" + entity.Id;
}

public sealed record CellDifference(bool Changed, object? Before, object? After)
{
    public static CellDifference None { get; } = new(false, null, null);
}
public sealed record RowState(bool IsDeleted, bool IsAdded);
public sealed record TableNotice(DateTimeOffset Time, string Message, bool IsError, Guid? ActionId = null);
public sealed record ReferenceOption(Guid Id, string Label, bool Selectable = true)
{
    public string? Description { get; init; }
}
public sealed record DependencyGroup(string Title, string ChildObjectType, string ForeignKey, Guid ParentId, int Count);
public sealed record DependencyEffect(string Title, TableKey Entity, IReadOnlyList<string> ClearedLinks);
public sealed record DeletePreview(bool Allowed, string? Reason, IReadOnlyList<DependencyGroup> Dependencies, IReadOnlyList<TableKey> Affected, string? Version = null)
{
    public IReadOnlyList<DependencyEffect> Effects { get; init; } = [];
}
public sealed record DataInvalidation(IReadOnlySet<string> ObjectTypes, long Version);