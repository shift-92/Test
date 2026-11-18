using Common.Ticket.Models;
using System.Reflection;
using System.Text.Json;

namespace Common.Ticket.Metadata;

public sealed record ReferenceMetadata(string Property, string TargetType, ReferenceDeleteBehavior DeleteBehavior);
public sealed class ModelMetadata
{
    public required Type ClrType { get; init; }
    public required string Name { get; init; }
    public Dictionary<string, PropertyInfo> Properties { get; } = new(StringComparer.Ordinal);
    public List<ReferenceMetadata> References { get; } = [];
    public HashSet<string> ExternalReferences { get; } = new(StringComparer.Ordinal);
}
public sealed class ModelRegistry
{
    private readonly Dictionary<Type, ModelMetadata> _types = [];
    private readonly Dictionary<string, ModelMetadata> _names = new(StringComparer.Ordinal);
    public IEnumerable<ModelMetadata> All => _names.Values;
    public ModelMetadata Get(Type type) => _types.TryGetValue(type, out var m) ? m : throw new ChangeValidationException($"Незарегистрированный тип {type}.");
    public ModelMetadata Get(string name) => _names.TryGetValue(name, out var m) ? m : throw new ChangeValidationException($"Незарегистрированный тип {name}.");
    public EntityKey Key(ITicketModel model) => new(Get(model.GetType()).Name, model.Id);
    public static ModelRegistry Scan(params Assembly[] assemblies) => FromTypes(assemblies.Distinct().SelectMany(x => x.GetTypes()).Where(x => x.IsClass && !x.IsAbstract && !x.ContainsGenericParameters && typeof(ITicketModel).IsAssignableFrom(x) && x.IsDefined(typeof(HistoryTypeAttribute), false)));
    public static ModelRegistry FromTypes(IEnumerable<Type> types)
    {
        var r = new ModelRegistry();
        foreach (var type in types.Distinct())
        {
            var name = type.GetCustomAttribute<HistoryTypeAttribute>()?.Name;
            if (!type.IsClass || type.IsAbstract || !typeof(ITicketModel).IsAssignableFrom(type) || string.IsNullOrWhiteSpace(name) || type.GetConstructor(Type.EmptyTypes) is null)
                throw new InvalidOperationException($"Недопустимая модель {type}.");
            var id = type.GetProperty(nameof(ITicketModel.Id));
            if (id?.PropertyType != typeof(Guid) || id.GetMethod?.IsPublic != true || id.SetMethod?.IsPublic != true) throw new InvalidOperationException($"Неверный идентификатор: {type}.");
            var m = new ModelMetadata { ClrType = type, Name = name };
            r._types.Add(type, m);
            if (!r._names.TryAdd(name, m)) throw new InvalidOperationException($"Дублирующееся имя типа {name}.");
        }
        foreach (var m in r.All)
        foreach (var p in m.ClrType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length != 0 || p.Name == nameof(ITicketModel.Id) || p.IsDefined(typeof(HistoryIgnoreAttribute)) || IsNavigation(p.PropertyType)) continue;
            if (!IsScalar(p.PropertyType) || p.GetMethod?.IsPublic != true || p.SetMethod?.IsPublic != true) throw new InvalidOperationException($"Неподдерживаемое свойство {m.Name}.{p.Name}. Используйте HistoryIgnore.");
            m.Properties.Add(p.Name, p);
            var main = p.GetCustomAttribute<MainObjectAttribute>();
            var deletion = p.GetCustomAttribute<HistoryDeleteBehaviorAttribute>();
            bool external = p.IsDefined(typeof(ExternalReferenceAttribute));
            if (main is null)
            {
                if (deletion is not null || external) throw new InvalidOperationException($"Для {m.Name}.{p.Name} требуется MainObject.");
                continue;
            }
            if (p.PropertyType != typeof(Guid) && p.PropertyType != typeof(Guid?)) throw new InvalidOperationException($"Внешний ключ (FK) должен иметь тип Guid/Guid?: {p.Name}.");
            var navigation = m.ClrType.GetProperty(main.NavigationProperty);
            if (navigation is null || !typeof(ITicketModel).IsAssignableFrom(navigation.PropertyType)) throw new InvalidOperationException($"Недопустимая навигация для {m.Name}.{p.Name}.");
            if (external)
            {
                if (deletion is not null) throw new InvalidOperationException("Внешняя ссылка не может иметь поведение удаления.");
                m.ExternalReferences.Add(p.Name);
                continue;
            }
            var behavior = deletion?.Behavior ?? ReferenceDeleteBehavior.Restrict;
            if (behavior == ReferenceDeleteBehavior.SetNull && p.PropertyType != typeof(Guid?)) throw new InvalidOperationException("Для SetNull требуется Guid?.");
            m.References.Add(new ReferenceMetadata(p.Name, r.Get(navigation.PropertyType).Name, behavior));
        }
        return r;
    }
    public static bool IsNavigation(Type t) => typeof(ITicketModel).IsAssignableFrom(t) || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>) && typeof(ITicketModel).IsAssignableFrom(t.GetGenericArguments()[0]));
    public static bool IsScalar(Type t)
    {
        t = Nullable.GetUnderlyingType(t) ?? t;
        return t.IsEnum || t == typeof(string) || t == typeof(bool) || t == typeof(char) || t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) || t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong) || t == typeof(float) || t == typeof(double) || t == typeof(decimal) || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(DateOnly) || t == typeof(TimeOnly) || t == typeof(TimeSpan);
    }
}
public static class ScalarValues
{
    public static object? Read(PropertyInfo p, JsonElement value)
    {
        try
        {
            if (value.ValueKind == JsonValueKind.Undefined) throw new JsonException("Неопределённый JSON.");
            if (value.ValueKind == JsonValueKind.Null && p.PropertyType.IsValueType && Nullable.GetUnderlyingType(p.PropertyType) is null) throw new JsonException("Значение null не допускается.");
            return JsonSerializer.Deserialize(value.GetRawText(), p.PropertyType, ChangeJson.Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or FormatException)
        {
            throw new ChangeValidationException($"Неверный {p.DeclaringType?.Name}.{p.Name} ({p.PropertyType.Name}): {value}; {ex.Message}", ex);
        }
    }
    public static bool Equal(PropertyInfo p, JsonElement a, JsonElement b) => Equals(Read(p, a), Read(p, b));
}
public static class FlatMapper
{
    public static EntityState CaptureRow(ModelRegistry registry, ITicketModel model)
    {
        var m = registry.Get(model.GetType());
        return new EntityState { Key = registry.Key(model), Properties = m.Properties.ToDictionary(x => x.Key, x => JsonSerializer.SerializeToElement(x.Value.GetValue(model), x.Value.PropertyType, ChangeJson.Options), StringComparer.Ordinal) };
    }
    public static EditorState Capture(ModelRegistry registry, IEnumerable<ITicketModel> rows) => StateJson.FromRows(rows.Select(x => CaptureRow(registry, x)));
    public static List<T> Materialize<T>(ModelRegistry registry, EditorState state, bool includeDeleted = true) where T : class, ITicketModel, new()
    {
        var m = registry.Get(typeof(T));
        return [.. state.Entities.Values.Where(x => x.Key.Type == m.Name && (includeDeleted || !x.IsDeleted)).Select(row =>
        {
            var model = new T { Id = row.Key.Id };
            foreach (var p in m.Properties) p.Value.SetValue(model, ScalarValues.Read(p.Value, row.Properties[p.Key]));
            return model;
        })];
    }
}