using Common.Ticket.Engine;
using Common.Ticket.Metadata;
using Common.Ticket.Models;
using System.Reflection;
using System.Text.Json;

namespace Common.Ticket.Services;

public enum EntityChangeKind { Added, Modified, Deleted }
public sealed record PropertyDifference(string Property, JsonElement Before, JsonElement After);
public sealed record EntityDifference(EntityKey Key, EntityChangeKind Kind, EntityState? Original, EntityState Current, IReadOnlyList<PropertyDifference> Properties);
public sealed class EditorStateDiffer(ModelRegistry registry)
{
    public IReadOnlyList<EntityDifference> Compare(EditorState original, EditorState current) => Compare(original, current, false);
    internal IReadOnlyList<EntityDifference> Compare(EditorState original, EditorState current, bool allowSchemaDrift)
    {
        foreach (var key in original.Entities.Keys)
            if (!current.Entities.ContainsKey(key)) throw new ChangeValidationException($"Отсутствует исходная строка {key}; используйте логическое удаление.");
        var result = new List<EntityDifference>();
        foreach (var row in current.Entities.Values)
        {
            if (!original.Entities.TryGetValue(row.Key, out var before))
            {
                if (!row.IsDeleted) result.Add(new(row.Key, EntityChangeKind.Added, null, row.Copy(), []));
                continue;
            }
            if (before.IsDeleted) throw new ChangeValidationException("Исходные строки должны быть активными.");
            if (row.IsDeleted)
            {
                result.Add(new(row.Key, EntityChangeKind.Deleted, before.Copy(), row.Copy(), []));
                continue;
            }
            var fields = new List<PropertyDifference>();
            foreach (var p in registry.Get(row.Key.Type).Properties)
            {
                if (!before.Properties.TryGetValue(p.Key, out var a) || !row.Properties.TryGetValue(p.Key, out var b))
                {
                    if (allowSchemaDrift) continue;
                    throw new ChangeValidationException("Несоответствие схемы снимка.");
                }
                if (!ScalarValues.Equal(p.Value, a, b)) fields.Add(new(p.Key, a.Clone(), b.Clone()));
            }
            if (fields.Count != 0) result.Add(new(row.Key, EntityChangeKind.Modified, before.Copy(), row.Copy(), fields));
        }
        return result;
    }
    internal EntityState PrepareInsert(EntityState source)
    {
        var metadata = registry.Get(source.Key.Type);
        var row = new EntityState { Key = source.Key };
        ITicketModel? defaults = null;
        foreach (var (name, property) in metadata.Properties)
        {
            if (source.Properties.TryGetValue(name, out var value)) row.Properties[name] = value.Clone();
            else
            {
                defaults ??= (ITicketModel)Activator.CreateInstance(metadata.ClrType)!;
                defaults.Id = source.Key.Id;
                var initial = property.GetValue(defaults);
                if ((initial is null && new NullabilityInfoContext().Create(property).WriteState == NullabilityState.NotNull) ||
                    (initial is Guid id && id == Guid.Empty && metadata.ExternalReferences.Contains(name)))
                    throw new ChangeValidationException($"Нет начального значения для {source.Key}.{name}.");
                row.Properties[name] = JsonSerializer.SerializeToElement(initial, property.PropertyType, ChangeJson.Options);
            }
        }
        return row;
    }
}
public enum MissingModifiedEntityBehavior { Recreate, Conflict }
public sealed record MergeResult(EditorState State, IReadOnlyList<EntityDifference> SessionChanges);
public sealed class SessionMergePlanner(EditorStateDiffer differ, HistoryEngine engine)
{
    // Фактические данные должны содержать соответствующий текущий совокупный показатель и относящиеся к нему внутренние ссылки.
    // Для вновь появившихся зависимых элементов каскадное действие не выполняется автоматически: проверка достоверности отклоняет «осиротевшие» связи.
    public MergeResult Merge(EditorState original, EditorState final, EditorState actual, MissingModifiedEntityBehavior missingBehavior = MissingModifiedEntityBehavior.Recreate)
    {
        engine.Validate(original, allowSchemaDrift: true);
        engine.Validate(final, allowSchemaDrift: true);
        engine.Validate(actual);
        var changes = differ.Compare(original, final, allowSchemaDrift: true);
        var merged = actual.Copy();
        foreach (var change in changes)
        {
            bool exists = merged.Entities.TryGetValue(change.Key, out var row) && !row.IsDeleted;
            switch (change.Kind)
            {
                case EntityChangeKind.Added:
                    if (merged.Entities.ContainsKey(change.Key)) throw new ChangeValidationException($"Новый идентификатор уже существует: {change.Key}.");
                    merged.Entities.Add(change.Key, differ.PrepareInsert(change.Current));
                    break;
                case EntityChangeKind.Deleted:
                    if (exists) row!.IsDeleted = true;
                    break;
                case EntityChangeKind.Modified:
                    if (!exists)
                    {
                        if (missingBehavior == MissingModifiedEntityBehavior.Conflict) throw new ChangeValidationException($"Измененная сущность была удалена извне: {change.Key}.");
                        merged.Entities[change.Key] = differ.PrepareInsert(change.Current);
                    }
                    else
                    {
                        foreach (var property in change.Properties) row!.Properties[property.Property] = property.After.Clone();
                    }
                    break;
            }
        }
        engine.Validate(merged);
        return new(merged, changes);
    }
}