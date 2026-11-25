using Common.Ticket.Metadata;
using Common.Ticket.Models;
using System.Text.Json;

namespace Common.Ticket.Engine;

public sealed record ParsedPatchPath(EntityKey Key, string Part, string? Property);
public static class Patch
{
    public static string Escape(string s) => s.Replace("~", "~0").Replace("/", "~1");
    public static string EntityPath(EntityKey key) => $"/entities/{Escape(key.Type)}/{key.Id:D}";
    public static PatchOperation Set<T>(EntityKey key, string property, T value) => new("replace", $"{EntityPath(key)}/properties/{Escape(property)}", ChangeJson.Value(value));
    public static PatchOperation Deleted(EntityKey key, bool value) => new("replace", $"{EntityPath(key)}/deleted", ChangeJson.Value(value));
    public static PatchOperation Add(EntityState row) => new("add", EntityPath(row.Key), ChangeJson.Value(row));
    public static ParsedPatchPath Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ChangeValidationException("Empty patch path.");
        var p = path.Split('/');
        if (p.Length < 4 || p[0] != "" || p[1] != "entities" || !Guid.TryParseExact(p[3], "D", out var id)) throw new ChangeValidationException($"Недопустимый путь {path}.");
        static string Decode(string s)
        {
            for (int i = 0; i < s.Length; i++) if (s[i] == '~' && (i + 1 >= s.Length || (s[i + 1] != '0' && s[i + 1] != '1'))) throw new ChangeValidationException("Недопустимая экранирующая последовательность в JSON-указателе.");
            return s.Replace("~1", "/").Replace("~0", "~");
        }
        var key = new EntityKey(Decode(p[2]), id);
        if (p.Length == 4) return new(key, "entity", null);
        if (p.Length == 5 && p[4] == "deleted") return new(key, "deleted", null);
        if (p.Length == 6 && p[4] == "properties") return new(key, "property", Decode(p[5]));
        throw new ChangeValidationException($"Неподдерживаемый путь {path}.");
    }
}
public sealed class HistoryEngine(ModelRegistry registry)
{
    public ApplyResult Apply(EditorState source, IReadOnlyList<PatchOperation> operations)
    {
        var state = source.Copy();
        var inverse = new List<PatchOperation>();
        foreach (var op in operations) inverse.Add(ApplyOne(state, op));
        Validate(state);
        inverse.Reverse();
        return new(state, inverse);
    }
    private PatchOperation ApplyOne(EditorState state, PatchOperation op)
    {
        var path = Patch.Parse(op.Path);
        var m = registry.Get(path.Key.Type);
        if (op.Op == "add" && path.Part == "entity")
        {
            var row = op.Value.Deserialize<EntityState>(ChangeJson.Options) ?? throw new ChangeValidationException("Отсутствует снимок сущности.");
            if (row.Key != path.Key || row.IsDeleted || state.Entities.ContainsKey(row.Key)) throw new ChangeValidationException("Недопустимая операция добавления.");
            ValidateRow(row);
            state.Entities.Add(row.Key, row.Copy());
            return Patch.Deleted(row.Key, true);
        }
        if (op.Op != "replace" || !state.Entities.TryGetValue(path.Key, out var entity)) throw new ChangeValidationException("Недопустимая операция или неизвестный объект.");
        if (path.Part == "deleted")
        {
            if (op.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ChangeValidationException("deleted должно быть логическим значением (boolean).");
            var old = entity.IsDeleted;
            entity.IsDeleted = op.Value.GetBoolean();
            return Patch.Deleted(path.Key, old);
        }
        if (path.Part != "property" || entity.IsDeleted || !m.Properties.TryGetValue(path.Property!, out var property)) throw new ChangeValidationException("Свойство недоступно или объект удален.");
        ScalarValues.Read(property, op.Value);
        var previous = entity.Properties[path.Property!];
        entity.Properties[path.Property!] = op.Value.Clone();
        return new("replace", op.Path, previous.Clone());
    }
    public void Validate(EditorState state) => Validate(state, false);
    internal void Validate(EditorState state, bool allowSchemaDrift)
    {
        foreach (var pair in state.Entities)
        {
            var row = pair.Value;
            if (pair.Key != row.Key) throw new ChangeValidationException("Несоответствие ключа состояния.");
            ValidateRow(row, allowSchemaDrift);
            if (row.IsDeleted) continue;
            foreach (var reference in registry.Get(row.Key.Type).References)
            {
                if (!row.Properties.TryGetValue(reference.Property, out var value)) continue;
                if (value.ValueKind == JsonValueKind.Null) continue;
                var target = new EntityKey(reference.TargetType, value.GetGuid());
                if (state.Entities.TryGetValue(target, out var entity) ? entity.IsDeleted : !reference.AllowOutsideSnapshot || target.Id == Guid.Empty)
                    throw new ChangeValidationException($"{row.Key}.{reference.Property} ссылается на отсутствующий/удаленный объект {target}.");
            }
        }
    }
    public void ValidateRow(EntityState row) => ValidateRow(row, false);
    private void ValidateRow(EntityState row, bool allowSchemaDrift)
    {
        if (row.Key.Id == Guid.Empty) throw new ChangeValidationException("Пустой идентификатор сущности.");
        var m = registry.Get(row.Key.Type);
        if (!allowSchemaDrift && (row.Properties.Count != m.Properties.Count || m.Properties.Keys.Any(x => !row.Properties.ContainsKey(x)))) throw new ChangeValidationException($"Несоответствие схемы для {row.Key}.");
        foreach (var p in m.Properties)
            if (row.Properties.TryGetValue(p.Key, out var value)) ScalarValues.Read(p.Value, value);
    }
}
public static class HistoryReplay
{
    public static EditorState Replay(HistoryEngine engine, EditorState original, IReadOnlyList<StoredAction> history)
    {
        var state = original.Copy();
        long sequence = 0;
        Guid? ticket = null;
        foreach (var action in history.OrderBy(x => x.Sequence))
        {
            ticket ??= action.TicketId;
            if (action.SchemaVersion != 1 || action.TicketId != ticket || action.Sequence != ++sequence) throw new ChangeValidationException("Некорректная или неполная история.");
            state = engine.Apply(state, action.Operations).State;
        }
        return state;
    }
}