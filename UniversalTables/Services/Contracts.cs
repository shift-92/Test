using UniversalTables.Models;

namespace UniversalTables.Services;

// All methods accessing cache/state run on the Blazor circuit unless the application explicitly provides synchronization.
public interface ITableDataSource
{
    TableContext Context { get; }
    IReadOnlyList<object> Rows(Type registeredType);
    // In Ticket mode: merge cache and state by Id, state wins, tombstones suppress cache entries.
    IReadOnlyList<ReferenceOption> ReferenceOptions(Type registeredType);
    ReferenceOption? ResolveReference(Type registeredType, Guid id);
    RowState GetRowState(TableKey key);
    CellDifference GetDifference(CellKey key);
    // A typed snapshot lets column templates render the previous value just like a table cell.
    object? GetOriginalRow(TableKey key) => null;
    bool CanEdit(TableKey key);
    bool CanRestore(TableKey key) => Context.Mode == TableMode.Ticket && GetRowState(key).IsDeleted && CanEdit(key);
    event Action<DataInvalidation>? Invalidated;
}

public interface ICommandTransport
{
    // Rejected is definitive. A lost response must be Unknown, not Rejected.
    Task<TransportReceipt> SendAsync(CommandEnvelope command, CancellationToken cancellationToken);
    // Raised after local cache/state has applied the corresponding server event.
    event Action<CommandCompletion>? Completed;
    // Null means not yet known, not "rejected". Must not mutate the request.
    Task<CommandCompletion?> GetOutcomeAsync(CommandEnvelope command, CancellationToken cancellationToken);
}

public interface ITableLayoutStore
{
    Task<LayoutSettings> LoadAsync(string settingsType, Guid userId, CancellationToken cancellationToken);
    Task SaveUserAsync(string settingsType, Guid userId, IReadOnlyList<ColumnLayout> columns, CancellationToken cancellationToken);
}

public interface IDeletionService
{
    // Live: inspect immediate child collections. Ticket: delegate to history planners.
    Task<DeletePreview> PreviewDeleteAsync(TableKey root, CancellationToken cancellationToken);
    Task<DeletePreview> PreviewRestoreAsync(TableKey root, CancellationToken cancellationToken);
}

public interface ITableDefaultsProvider
{
    // Explicit field defaults only. UI does not instantiate or mutate domain models.
    IReadOnlyDictionary<string, object?> Defaults(Type modelType, ChangeKind kind);
}

public sealed class EmptyTableDefaultsProvider : ITableDefaultsProvider
{
    public IReadOnlyDictionary<string, object?> Defaults(Type modelType, ChangeKind kind) => new Dictionary<string, object?>();
}