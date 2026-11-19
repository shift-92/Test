using System.Collections;
using System.Reflection;
using UniversalTables.Models;

namespace UniversalTables.Services;

public static class RowRelations
{
    // Перестраивайте навигационные свойства для строк, принадлежащих снимку (snapshot); никогда не изменяйте строки в общем кэше.
    public static void Rebuild(IReadOnlyDictionary<Type, IReadOnlyList<object>> rows)
    {
        var byId = rows.ToDictionary(pair => pair.Key, pair => pair.Value.ToDictionary(row => ((ITableModel)row).Id));
        foreach (var (type, instances) in rows)
        {
            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (ColumnMetadata.StringArgument(property, nameof(MainObjectAttribute)) is string navigationName)
                {
                    ValidateKey(property);
                    var navigation = type.GetProperty(navigationName);
                    if (navigation?.SetMethod is null || !typeof(ITableModel).IsAssignableFrom(navigation.PropertyType)) throw new InvalidOperationException($"Недопустимая навигация: {type.Name}.{navigationName}");

                    byId.TryGetValue(navigation.PropertyType, out var targets);
                    foreach (var row in instances)
                        navigation.SetValue(row, property.GetValue(row) is Guid id ? targets?.GetValueOrDefault(id) : null);
                }

                if (ColumnMetadata.StringArgument(property, nameof(PredictFieldAttribute)) is not string keyName) continue;
                var listType = property.PropertyType;
                if (!listType.IsGenericType || listType.GetGenericTypeDefinition() != typeof(List<>)) throw new InvalidOperationException($"Ожидалась коллекция List<T>: {type.Name}.{property.Name}");

                var childType = listType.GetGenericArguments()[0];
                var key = childType.GetProperty(keyName) ?? throw new InvalidOperationException($"Неизвестный внешний ключ: {childType.Name}.{keyName}");
                ValidateKey(key);
                var children = rows.GetValueOrDefault(childType) ?? Array.Empty<object>();
                var childrenByParent = children.ToLookup(child => (Guid?)key.GetValue(child));
                foreach (var parent in instances)
                {
                    if (property.GetValue(parent) is not IList list) throw new InvalidOperationException($"Коллекция не инициализирована: {type.Name}.{property.Name}");
                    list.Clear();
                    foreach (var child in childrenByParent[((ITableModel)parent).Id]) list.Add(child);
                }
            }
        }
    }

    private static void ValidateKey(PropertyInfo key)
    {
        if (key.PropertyType != typeof(Guid) && key.PropertyType != typeof(Guid?)) throw new InvalidOperationException($"FK должен быть Guid или Guid?: {key.DeclaringType?.Name}.{key.Name}");
    }
}