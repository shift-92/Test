using UniversalTables.Models;

namespace UniversalTables.Services;

// Explicit application boundaries; no fake success or reflection-based calls into unknown services.
public sealed class DelegateCommandTransport(
    Func<CommandEnvelope,CancellationToken,Task<TransportReceipt>> send,
    Func<CommandEnvelope,CancellationToken,Task<CommandCompletion?>> outcome) : ICommandTransport
{
    public event Action<CommandCompletion>? Completed;
    public Task<TransportReceipt> SendAsync(CommandEnvelope command,CancellationToken token)=>send(command,token);
    public Task<CommandCompletion?> GetOutcomeAsync(CommandEnvelope command,CancellationToken token)=>outcome(command,token);
    public void PublishAfterStateUpdate(CommandCompletion completion)=>Completed?.Invoke(completion);
}
public sealed class DelegateLayoutStore(
    Func<string,Guid,CancellationToken,Task<LayoutSettings>> load,
    Func<string,Guid,IReadOnlyList<ColumnLayout>,CancellationToken,Task> save) : ITableLayoutStore
{
    public Task<LayoutSettings> LoadAsync(string settingsType,Guid userId,CancellationToken token)=>load(settingsType,userId,token);
    public Task SaveUserAsync(string settingsType,Guid userId,IReadOnlyList<ColumnLayout> columns,CancellationToken token)=>save(settingsType,userId,columns,token);
}
public sealed class DelegateDeletionService(
    Func<TableKey,CancellationToken,Task<DeletePreview>> delete,
    Func<TableKey,CancellationToken,Task<DeletePreview>> restore) : IDeletionService
{
    public Task<DeletePreview> PreviewDeleteAsync(TableKey root,CancellationToken token)=>delete(root,token);
    public Task<DeletePreview> PreviewRestoreAsync(TableKey root,CancellationToken token)=>restore(root,token);
}