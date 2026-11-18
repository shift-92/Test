using Common.Ticket.Engine;
using Common.Ticket.Metadata;
using Common.Ticket.Models;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;

namespace Common.Ticket.Services;

public enum ChangeDetailKind { Added, PropertyChanged, Deleted, Restored }
public sealed record ChangeDetail(ChangeDetailKind Kind, EntityKey Entity, string EntityType, string EntityName, string? Property, string? PropertyTitle, string? Before, string? After);
public sealed record ChangeLogEntry(long Sequence, Guid ActionId, DateTimeOffset CreatedAt, string Author, string Title, IReadOnlyList<ChangeDetail> Details);
public enum WorkItemKind { Create, Modify, Remove }
public sealed record WorkFieldChange(string Property, string Title, string Before, string After);
public sealed record WorkItem(WorkItemKind Kind, EntityKey Entity, string EntityType, string EntityName, string Title, IReadOnlyList<WorkFieldChange> Fields);
public sealed record ChangeViews(EditorState Current, IReadOnlyList<ChangeLogEntry> Log, IReadOnlyList<WorkItem> Work);
public sealed class ChangePresentationOptions
{
    public Func<EntityKey, string?>? EntityName { get; init; }
    public Func<string, string, Guid, string?>? ReferenceName { get; init; }
    public Func<WorkItemKind, string, string?>? WorkTitle { get; init; }
}
public sealed class ChangePresenter(ModelRegistry registry, ChangePresentationOptions options)
{
    public string TypeTitle(string type) => registry.Get(type).ClrType.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? registry.Get(type).ClrType.Name;
    public string PropertyTitle(string type, string property) => registry.Get(type).Properties.TryGetValue(property, out var p) ? p.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? property : property;
    public string EntityTitle(EntityKey key, EditorState state)
    {
        var custom = options.EntityName?.Invoke(key);
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        if (state.Entities.TryGetValue(key, out var row))
        foreach (var field in new[] { "Name", "Address", "Title" })
            if (row.Properties.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) return value.GetString()!;
        return key.Id.ToString();
    }
    public string Value(EntityKey key, string property, JsonElement value, EditorState state)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "—";
        if (value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id))
        {
            var custom = options.ReferenceName?.Invoke(key.Type, property, id);
            if (!string.IsNullOrWhiteSpace(custom)) return $"{custom} ({id})";
            var reference = registry.Get(key.Type).References.SingleOrDefault(x => x.Property == property);
            if (reference is not null)
            {
                var target = new EntityKey(reference.TargetType, id);
                if (state.Entities.ContainsKey(target)) return $"{EntityTitle(target, state)} ({id})";
            }
        }
        return value.ValueKind switch { JsonValueKind.String => value.GetString() ?? "", JsonValueKind.True => "Да", JsonValueKind.False => "Нет", _ => value.GetRawText() };
    }
    public string WorkTitle(WorkItemKind kind, string type) => options.WorkTitle?.Invoke(kind, type) ?? kind switch
    {
        WorkItemKind.Create => "Создать",
        WorkItemKind.Modify => "Изменить параметры",
        WorkItemKind.Remove => "Удалить",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };
}
public sealed class ChangeViewBuilder(HistoryEngine engine, EditorStateDiffer differ, ChangePresenter presenter)
{
    public ChangeViews Build(EditorState original, IReadOnlyList<StoredAction> history)
    {
        var current = original.Copy();
        var log = new List<ChangeLogEntry>();
        long sequence = 0;
        Guid? ticket = null;
        foreach (var action in history.OrderBy(x => x.Sequence))
        {
            ticket ??= action.TicketId;
            if (action.SchemaVersion != 1 || action.TicketId != ticket || action.Sequence != ++sequence) throw new ChangeValidationException("Требуется полная история заявок.");
            var applied = engine.Apply(current, action.Operations);
            var display = current.Copy();
            var details = new List<ChangeDetail>();
            foreach (var op in action.Operations)
            {
                var path = Patch.Parse(op.Path);
                var key = path.Key;
                if (op.Op == "add")
                {
                    var row = op.Value.Deserialize<EntityState>(ChangeJson.Options)!;
                    display.Entities.Add(key, row.Copy());
                    details.Add(new(ChangeDetailKind.Added, key, presenter.TypeTitle(key.Type), presenter.EntityTitle(key, display), null, null, null, "Создан"));
                    foreach (var property in row.Properties) details.Add(new(ChangeDetailKind.PropertyChanged, key, presenter.TypeTitle(key.Type), presenter.EntityTitle(key, display), property.Key, presenter.PropertyTitle(key.Type, property.Key), "—", presenter.Value(key, property.Key, property.Value, display)));
                    continue;
                }
                var entity = display.Entities[key];
                var title = presenter.EntityTitle(key, display);
                if (path.Part == "deleted")
                {
                    var deleted = op.Value.GetBoolean();
                    details.Add(new(deleted ? ChangeDetailKind.Deleted : ChangeDetailKind.Restored, key, presenter.TypeTitle(key.Type), title, null, "Состояние", entity.IsDeleted ? "Удалён" : "Активен", deleted ? "Удалён" : "Активен"));
                    entity.IsDeleted = deleted;
                }
                else
                {
                    var property = path.Property!;
                    details.Add(new(ChangeDetailKind.PropertyChanged, key, presenter.TypeTitle(key.Type), title, property, presenter.PropertyTitle(key.Type, property), presenter.Value(key, property, entity.Properties[property], display), presenter.Value(key, property, op.Value, display)));
                    entity.Properties[property] = op.Value.Clone();
                }
            }
            log.Add(new(action.Sequence, action.ActionId, action.CreatedAt, action.UserName ?? action.UserId ?? "Не указан", action.Title, details));
            current = applied.State;
        }
        return new(current, log, BuildWork(original, current));
    }
    public IReadOnlyList<WorkItem> BuildWork(EditorState original, EditorState current)
    {
        return [.. differ.Compare(original, current).Select(change =>
        {
            var kind = change.Kind switch { EntityChangeKind.Added => WorkItemKind.Create, EntityChangeKind.Modified => WorkItemKind.Modify, _ => WorkItemKind.Remove };
            var fields = new List<WorkFieldChange>();
            if (kind == WorkItemKind.Create)
                fields.AddRange(change.Current.Properties.Select(x => new WorkFieldChange(x.Key, presenter.PropertyTitle(change.Key.Type, x.Key), "—", presenter.Value(change.Key, x.Key, x.Value, current))));
            else if (kind == WorkItemKind.Modify)
                fields.AddRange(change.Properties.Select(x => new WorkFieldChange(x.Property, presenter.PropertyTitle(change.Key.Type, x.Property), presenter.Value(change.Key, x.Property, x.Before, original), presenter.Value(change.Key, x.Property, x.After, current))));
            return new WorkItem(kind, change.Key, presenter.TypeTitle(change.Key.Type), presenter.EntityTitle(change.Key, kind == WorkItemKind.Create ? current : original), presenter.WorkTitle(kind, change.Key.Type), fields);
        })];
    }
}