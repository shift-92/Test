using Common.Ticket.Engine;
using Common.Ticket.Metadata;
using Common.Ticket.Models;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;

namespace Common.Ticket.Services;

public sealed record CopyRelation(Type ChildType, string ForeignKeyProperty, IReadOnlyList<CopyRelation> Children)
{
    public static CopyRelation Of<T>(Expression<Func<T, object?>> selector, params CopyRelation[] children) where T : class, ITicketModel
    {
        Expression body = selector.Body;
        if (body is UnaryExpression { NodeType: ExpressionType.Convert } u) body = u.Operand;
        if (body is not MemberExpression { Member: PropertyInfo p, Expression: ParameterExpression }) throw new ArgumentException("Используйте селектор прямого свойства внешнего ключа (FK).");
        return new(typeof(T), p.Name, children);
    }
}
public sealed record PreparedAddition(EntityKey Root, IReadOnlyList<EntityState> Rows, IReadOnlyDictionary<EntityKey, EntityKey> Copies)
{
    public List<PatchOperation> ToOperations() => [.. Rows.Select(Patch.Add)];
}
public sealed class EditorObjectFactory(ModelRegistry registry)
{
    public PreparedAddition Create<T>(IReadOnlyDictionary<string, object?>? values = null) where T : class, ITicketModel => Create(typeof(T), values);
    public PreparedAddition Create(string type, IReadOnlyDictionary<string, object?>? values = null) => Create(registry.Get(type).ClrType, values);
    public PreparedAddition Create(Type type, IReadOnlyDictionary<string, object?>? values = null)
    {
        var m = registry.Get(type);
        var model = (ITicketModel)Activator.CreateInstance(m.ClrType)!;
        model.Id = Guid.NewGuid();
        var row = FlatMapper.CaptureRow(registry, model);
        ApplyValues(row, values);
        return new(row.Key, [row], new Dictionary<EntityKey, EntityKey>());
    }
    public PreparedAddition Copy(EditorState state, EntityKey source, IReadOnlyList<CopyRelation>? relations = null, IReadOnlyDictionary<string, object?>? rootValues = null)
    {
        if (!state.Entities.TryGetValue(source, out var sourceRow) || sourceRow.IsDeleted) throw new ChangeValidationException("Источник отсутствует или удален.");
        var selected = new HashSet<EntityKey> { source };
        var recursion = new HashSet<CopyRelation>(ReferenceEqualityComparer.Instance);
        void Collect(EntityKey parent, IReadOnlyList<CopyRelation> branches)
        {
            foreach (var branch in branches)
            {
                if (!recursion.Add(branch)) throw new ChangeValidationException("Циклический выбор копий.");
                var child = registry.Get(branch.ChildType);
                var reference = child.References.SingleOrDefault(x => x.Property == branch.ForeignKeyProperty && x.TargetType == parent.Type) ?? throw new ChangeValidationException("Выбор для копирования не является внутренним внешним ключом (FK).");
                foreach (var row in state.Entities.Values.Where(x => !x.IsDeleted && x.Key.Type == child.Name))
                {
                    var value = row.Properties[reference.Property];
                    if (value.ValueKind == JsonValueKind.Null || value.GetGuid() != parent.Id) continue;
                    selected.Add(row.Key);
                    Collect(row.Key, branch.Children);
                }
                recursion.Remove(branch);
            }
        }
        Collect(source, relations ?? []);
        var map = selected.ToDictionary(x => x, x => new EntityKey(x.Type, Guid.NewGuid()));
        var rows = new List<EntityState>();
        foreach (var key in selected)
        {
            var copy = new EntityState { Key = map[key], Properties = state.Entities[key].Properties.ToDictionary(x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal) };
            foreach (var reference in registry.Get(key.Type).References)
            {
                var value = copy.Properties[reference.Property];
                if (value.ValueKind == JsonValueKind.Null) continue;
                if (map.TryGetValue(new(reference.TargetType, value.GetGuid()), out var target)) copy.Properties[reference.Property] = ChangeJson.Value(target.Id);
            }
            rows.Add(copy);
        }
        ApplyValues(rows.Single(x => x.Key == map[source]), rootValues);
        return new(map[source], rows, map);
    }
    private void ApplyValues(EntityState row, IReadOnlyDictionary<string, object?>? values)
    {
        if (values is null) return;
        var metadata = registry.Get(row.Key.Type);
        foreach (var pair in values)
        {
            if (!metadata.Properties.TryGetValue(pair.Key, out var p)) throw new ChangeValidationException($"Свойство недоступно для редактирования: {pair.Key}.");
            var json = pair.Value is JsonElement e ? e.Clone() : JsonSerializer.SerializeToElement(pair.Value, ChangeJson.Options);
            var typed = ScalarValues.Read(p, json);
            row.Properties[pair.Key] = JsonSerializer.SerializeToElement(typed, p.PropertyType, ChangeJson.Options);
        }
    }
}
public sealed class ChangeCommands(ModelRegistry registry)
{
    public PatchOperation SetProperty<T, TValue>(T model, Expression<Func<T, TValue>> selector, TValue value) where T : class, ITicketModel
    {
        if (selector.Body is not MemberExpression { Member: PropertyInfo p, Expression: ParameterExpression }) throw new ArgumentException("Используйте прямой селектор свойства.");
        var m = registry.Get(model.GetType());
        if (!m.Properties.ContainsKey(p.Name)) throw new ChangeValidationException("Свойство не отслеживается.");
        return Patch.Set(registry.Key(model), p.Name, value);
    }
}