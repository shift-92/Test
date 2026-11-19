using Common.Ticket.Models;
using Common.Ticket.Engine;
using Common.Ticket.Metadata;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;

namespace Front.Components.Shared;

public sealed record UiDetail(string Operation, EntityKey Entity, string EntityName, string Field, string Before, string After);
public sealed record UiLog(long Sequence, Guid ActionId, DateTimeOffset Date, string Author, string Title, List<UiDetail> Details);
public sealed record UiField(string Name, string Before, string After);
public sealed record UiWork(string Kind, EntityKey Entity, string TypeName, string Name, List<UiField> Fields);
public sealed class UiPresentation
{
    public Func<string, string, Guid, string?>? ExternalReferenceName { get; set; }
}

public sealed class ChangesViewService(ModelRegistry registry, HistoryEngine engine)
{
    public string TypeName(string type) => registry.Get(type).ClrType.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? type;
    public string PropertyName(string type, string name) => registry.Get(type).Properties[name].GetCustomAttribute<DisplayAttribute>()?.GetName() ?? name;
    public string EntityName(EntityKey key, EditorState state) => state.Entities.TryGetValue(key, out var row) &&
        (row.Properties.TryGetValue("Name", out var name) || row.Properties.TryGetValue("Address", out name))
        ? name.ToString() : key.Id.ToString();
    public string Value(EntityKey key, string property, JsonElement value, EditorState state)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return "—";
        var reference = registry.Get(key.Type).References.FirstOrDefault(r => r.Property == property);
        return reference is not null ? EntityName(new(reference.TargetType, value.GetGuid()), state) : value.ToString();
    }

    public List<UiLog> BuildLog(EditorState original, IReadOnlyList<StoredAction> history)
    {
        var state = original;
        List<UiLog> entries = [];
        foreach (var action in history.OrderBy(a => a.Sequence))
        {
            var next = engine.Apply(state, action.Operations).State;
            List<UiDetail> details = [];
            for (var i = 0; i < action.Operations.Count; i++)
            {
                var op = action.Operations[i];
                var path = Patch.Parse(op.Path);
                var key = path.Key;
                var name = EntityName(key, next);
                if (path.Part == "entity")
                {
                    details.Add(new("Добавление", key, name, "Состояние", "—", "Добавлен"));
                    var added = op.Value.Deserialize<EntityState>(ChangeJson.Options)!;
                    foreach (var (field, value) in added.Properties)
                        details.Add(new("Начальное значение", key, name, PropertyName(key.Type, field), "—", Value(key, field, value, next)));
                }
                else if (path.Property is { } property)
                {
                    var before = action.InverseOperations[^(i + 1)].Value;
                    details.Add(new("Изменение", key, name, PropertyName(key.Type, property), Value(key, property, before, state), Value(key, property, op.Value, next)));
                }
                else
                {
                    var deleted = op.Value.GetBoolean();
                    details.Add(new(deleted ? "Удаление" : "Восстановление", key, name, "Состояние", deleted ? "Активен" : "Удалён", deleted ? "Удалён" : "Активен"));
                }
            }
            entries.Add(new(action.Sequence, action.ActionId, action.CreatedAt, action.UserName ?? action.UserId ?? "—", action.Title, details));
            state = next;
        }
        return entries;
    }

    public List<UiWork> BuildWork(EditorState original, EditorState current)
    {
        List<UiWork> items = [];
        foreach (var key in original.Entities.Keys.Union(current.Entities.Keys).OrderBy(k => k.Type).ThenBy(k => k.Id))
        {
            original.Entities.TryGetValue(key, out var before);
            current.Entities.TryGetValue(key, out var after);
            if (before?.IsDeleted == true) before = null;
            if (after?.IsDeleted == true) after = null;
            if (before is null && after is null) continue;
            List<UiField> fields = [];
            if (after is not null)
                foreach (var (property, value) in after.Properties)
                {
                    var old = before is null ? default : before.Properties[property];
                    if (before is null || !JsonElement.DeepEquals(old, value))
                        fields.Add(new(PropertyName(key.Type, property), Value(key, property, old, original), Value(key, property, value, current)));
                }
            var kind = before is null ? "create" : after is null ? "remove" : "modify";
            if (kind != "modify" || fields.Count != 0)
                items.Add(new(kind, key, TypeName(key.Type), EntityName(key, after is null ? original : current), fields));
        }
        return items;
    }
}