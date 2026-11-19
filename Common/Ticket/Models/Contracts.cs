using System.Text.Json;
using System.Text.Json.Serialization;

namespace Common.Ticket.Models;

public interface ITicketModel { Guid Id { get; set; } }
public readonly record struct EntityKey(string Type, Guid Id);
public enum ReferenceDeleteBehavior { Restrict, SetNull, Cascade }
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class HistoryTypeAttribute(string name) : Attribute { public string Name { get; } = name; }
[AttributeUsage(AttributeTargets.Property)]
public sealed class HistoryIgnoreAttribute : Attribute { }
[AttributeUsage(AttributeTargets.Property)]
public sealed class MainObjectAttribute(string navigationProperty) : Attribute
{
    public string NavigationProperty { get; } = navigationProperty;
    public bool AllowOutsideSnapshot { get; set; }
}
[AttributeUsage(AttributeTargets.Property)]
public sealed class ExternalReferenceAttribute : Attribute { }
[AttributeUsage(AttributeTargets.Property)]
public sealed class HistoryDeleteBehaviorAttribute(ReferenceDeleteBehavior behavior) : Attribute { public ReferenceDeleteBehavior Behavior { get; } = behavior; }

public sealed class EntityState
{
    public required EntityKey Key { get; init; }
    public bool IsDeleted { get; set; }
    public Dictionary<string, JsonElement> Properties { get; init; } = new(StringComparer.Ordinal);
    public EntityState Copy() => new() { Key = Key, IsDeleted = IsDeleted, Properties = Properties.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal) };
}
public sealed class EditorState
{
    public Dictionary<EntityKey, EntityState> Entities { get; } = [];
    public EditorState Copy() => StateJson.FromRows(Entities.Values);
}
public static class ChangeJson
{
    public static JsonSerializerOptions Options { get; } = Create();
    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        o.Converters.Add(new JsonStringEnumConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }
    public static JsonElement Value<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
public static class StateJson
{
    public static string Serialize(EditorState state) => JsonSerializer.Serialize(state.Entities.Values.OrderBy(x => x.Key.Type).ThenBy(x => x.Key.Id).ToArray(), ChangeJson.Options);
    public static EditorState Deserialize(string json) => FromRows(JsonSerializer.Deserialize<List<EntityState>>(json, ChangeJson.Options) ?? throw new ChangeValidationException("Empty state."));
    public static EditorState FromRows(IEnumerable<EntityState> rows)
    {
        var state = new EditorState();
        foreach (var row in rows)
            if (!state.Entities.TryAdd(row.Key, row.Copy())) throw new ChangeValidationException($"Duplicate entity {row.Key}.");
        return state;
    }
}
public sealed record PatchOperation(string Op, string Path, JsonElement Value);
public sealed record DeletionGroup(Guid GroupId, List<EntityKey> Roots, List<EntityKey> Entities);
public sealed class ChangeCommand
{
    public int SchemaVersion { get; init; } = 1;
    public Guid ActionId { get; init; }
    public Guid TicketId { get; init; }
    public long ExpectedSequence { get; init; }
    public string Kind { get; init; } = "";
    public string Title { get; init; } = "";
    public EntityKey? Entity { get; init; }
    public List<EntityKey> Entities { get; init; } = [];
    public long? TargetSequence { get; init; }
    public List<PatchOperation> Operations { get; init; } = [];
}
public sealed record StoredAction(int SchemaVersion, Guid ActionId, Guid TicketId, long Sequence, DateTimeOffset CreatedAt, string Title, List<PatchOperation> Operations, List<PatchOperation> InverseOperations, string? UserId = null, string? UserName = null, List<DeletionGroup>? DeletionGroups = null);
public sealed record PlannedChange(List<PatchOperation> Operations, List<DeletionGroup> DeletionGroups);
public sealed record ApplyResult(EditorState State, List<PatchOperation> InverseOperations);
public sealed record SessionInfoDto(Guid SessionId, long LastSequence, bool IsCompleted, List<EntityState> OriginalRows);
public sealed record HistoryPageDto(long HeadSequence, List<StoredAction> Actions, bool HasMore);
public sealed record ChangeErrorDto(string Code, string Message, long? ActualSequence = null);
public sealed record CompleteSessionRequest(long ExpectedSequence);
public sealed record CompleteSessionResponse(Guid SessionId, long FinalSequence, DateTimeOffset CompletedAt, string CompletedBy);
public class ChangeValidationException : Exception
{
    public ChangeValidationException(string message) : base(message) { }
    public ChangeValidationException(string message, Exception inner) : base(message, inner) { }
}
public sealed class SequenceConflictException(long actualSequence) : Exception($"Конфликт последовательностей: {actualSequence}.") { public long ActualSequence { get; } = actualSequence; }
public interface IChangesApiClient
{
    Task<SessionInfoDto> GetSessionAsync(Guid ticketId, CancellationToken ct = default);
    Task<HistoryPageDto> GetActionsAsync(Guid ticketId, long afterSequence, CancellationToken ct = default);
    Task<StoredAction> SubmitAsync(ChangeCommand command, CancellationToken ct = default);
    Task<CompleteSessionResponse> CompleteAsync(Guid ticketId, long expectedSequence, CancellationToken ct = default);
}
public interface IFinalStateValidator
{
    Task ValidateAsync(EditorState mergedState, CancellationToken ct);
}