using Common.Ticket.Metadata;
using Common.Ticket.Models;
using System.Text.Json;

namespace Common.Ticket.Engine;

public sealed class BulkDeletePlanner(ModelRegistry registry)
{
    private sealed record Incoming(EntityKey Source, ReferenceMetadata Reference);
    private sealed class Group
    {
        public HashSet<EntityKey> Roots { get; } = [];
        public HashSet<EntityKey> Entities { get; } = [];
    }
    public PlannedChange Plan(EditorState state, IEnumerable<EntityKey> requestedRoots)
    {
        var roots = requestedRoots.Distinct().ToArray();
        if (roots.Length == 0) throw new ChangeValidationException("Объекты не выбраны.");
        var incoming = new Dictionary<EntityKey, List<Incoming>>();
        foreach (var row in state.Entities.Values.Where(x => !x.IsDeleted))
        foreach (var reference in registry.Get(row.Key.Type).References)
        {
            var value = row.Properties[reference.Property];
            if (value.ValueKind == JsonValueKind.Null) continue;
            var target = new EntityKey(reference.TargetType, value.GetGuid());
            if (!incoming.TryGetValue(target, out var list)) incoming.Add(target, list = []);
            list.Add(new(row.Key, reference));
        }
        var groups = new List<Group>();
        foreach (var root in roots)
        {
            if (!state.Entities.TryGetValue(root, out var row) || row.IsDeleted) throw new ChangeValidationException($"Недоступный объект {root}.");
            var group = new Group();
            group.Roots.Add(root);
            group.Entities.Add(root);
            var queue = new Queue<EntityKey>();
            queue.Enqueue(root);
            while (queue.TryDequeue(out var target))
            {
                if (!incoming.TryGetValue(target, out var refs)) continue;
                foreach (var item in refs)
                    if (item.Reference.DeleteBehavior == ReferenceDeleteBehavior.Cascade && group.Entities.Add(item.Source)) queue.Enqueue(item.Source);
            }
            groups.Add(group);
        }
        bool merged;
        do
        {
            merged = false;
            for (int i = 0; i < groups.Count && !merged; i++)
            for (int j = i + 1; j < groups.Count; j++)
            {
                if (!groups[i].Entities.Overlaps(groups[j].Entities)) continue;
                groups[i].Entities.UnionWith(groups[j].Entities);
                groups[i].Roots.UnionWith(groups[j].Roots);
                groups.RemoveAt(j);
                merged = true;
                break;
            }
        } while (merged);
        var deleting = groups.SelectMany(x => x.Entities).ToHashSet();
        var operations = new List<PatchOperation>();
        var cleared = new HashSet<(EntityKey, string)>();
        foreach (var target in deleting)
        {
            if (!incoming.TryGetValue(target, out var refs)) continue;
            foreach (var item in refs.Where(x => !deleting.Contains(x.Source)))
            {
                if (item.Reference.DeleteBehavior == ReferenceDeleteBehavior.Restrict) throw new ChangeValidationException($"Удаление ограничено: {item.Source}.{item.Reference.Property}.");
                if (item.Reference.DeleteBehavior == ReferenceDeleteBehavior.SetNull && cleared.Add((item.Source, item.Reference.Property))) operations.Add(Patch.Set<Guid?>(item.Source, item.Reference.Property, null));
            }
        }
        operations.AddRange(deleting.OrderBy(x => x.Type).ThenBy(x => x.Id).Select(x => Patch.Deleted(x, true)));
        return new(operations, [.. groups.Select(x => new DeletionGroup(Guid.NewGuid(), [.. x.Roots], [.. x.Entities]))]);
    }
}
public sealed class RestorePlanner(HistoryEngine engine)
{
    private readonly record struct GroupKey(Guid ActionId, Guid GroupId);
    public List<PatchOperation> Plan(EditorState original, IReadOnlyList<StoredAction> history, IEnumerable<EntityKey> requestedRoots)
    {
        var state = original.Copy();
        var deletedBy = new Dictionary<EntityKey, GroupKey>();
        long sequence = 0;
        Guid? ticket = null;
        foreach (var action in history.OrderBy(x => x.Sequence))
        {
            ticket ??= action.TicketId;
            if (action.SchemaVersion != 1 || action.TicketId != ticket || action.Sequence != ++sequence) throw new ChangeValidationException("Неполная история.");
            var groups = new Dictionary<EntityKey, GroupKey>();
            foreach (var group in action.DeletionGroups ?? [])
            foreach (var key in group.Entities)
                if (!groups.TryAdd(key, new(action.ActionId, group.GroupId))) throw new ChangeValidationException("Перекрывающиеся сохраненные группы для удаления.");
            var next = engine.Apply(state, action.Operations).State;
            foreach (var pair in next.Entities)
            {
                bool wasDeleted = state.Entities.TryGetValue(pair.Key, out var old) && old.IsDeleted;
                if (!wasDeleted && pair.Value.IsDeleted) deletedBy[pair.Key] = groups.GetValueOrDefault(pair.Key, new(action.ActionId, Guid.Empty));
                else if (!pair.Value.IsDeleted) deletedBy.Remove(pair.Key);
            }
            state = next;
        }
        var roots = requestedRoots.Distinct().ToArray();
        if (roots.Length == 0) throw new ChangeValidationException("Объекты не выбраны.");
        var selected = new HashSet<GroupKey>();
        foreach (var root in roots)
        {
            if (!state.Entities.TryGetValue(root, out var row) || !row.IsDeleted || !deletedBy.TryGetValue(root, out var group)) throw new ChangeValidationException($"Для {root} активное удаление отсутствует.");
            selected.Add(group);
        }
        return [.. deletedBy.Where(x => selected.Contains(x.Value)).Select(x => x.Key).OrderBy(x => x.Type).ThenBy(x => x.Id).Select(x => Patch.Deleted(x, false))];
    }
}
public sealed class CommandPlanner(BulkDeletePlanner deletes, RestorePlanner restores)
{
    public const int MaxOperations = 10000;
    public static void ValidateCommand(ChangeCommand command)
    {
        if (command.SchemaVersion != 1 || command.ActionId == Guid.Empty || command.TicketId == Guid.Empty || command.ExpectedSequence < 0 || string.IsNullOrWhiteSpace(command.Title) || command.Title.Length > 500 || command.Entities is null || command.Entities.Count > 1000 || command.Operations is null || command.Operations.Count > MaxOperations)
            throw new ChangeValidationException("Неверная команда.");
    }
    public PlannedChange Plan(ChangeCommand command, EditorState original, EditorState current, IReadOnlyList<StoredAction> history, long head)
    {
        ValidateCommand(command);
        if (command.ExpectedSequence != head) throw new SequenceConflictException(head);
        EntityKey Root() => command.Entity ?? throw new ChangeValidationException("Необходимо указать сущность.");
        PlannedChange result;
        switch (command.Kind)
        {
            case "patch":
                // Изменения жизненного цикла должны выполняться с использованием явных команд удаления/восстановления.
                if (command.Operations.Any(x => Patch.Parse(x.Path).Part == "deleted")) throw new ChangeValidationException("Используйте команды удаления/восстановления для внесения изменений в жизненный цикл.");
                result = new([.. command.Operations], []);
                break;
            case "delete": result = deletes.Plan(current, [Root()]); break;
            case "deleteMany": result = deletes.Plan(current, command.Entities); break;
            case "restore":
            case "restoreMany":
                EnsureFullHistory(history, head, command.TicketId);
                result = new(restores.Plan(original, history, command.Kind == "restore" ? new[] { Root() } : command.Entities), []);
                break;
            case "rollback":
                EnsureFullHistory(history, head, command.TicketId);
                var target = command.TargetSequence ?? throw new ChangeValidationException("Требуется TargetSequence.");
                if (target < 0 || target > head) throw new ChangeValidationException("Недопустимая целевая последовательность.");
                result = new([.. history.Where(x => x.Sequence > target).OrderByDescending(x => x.Sequence).SelectMany(x => x.InverseOperations)], []);
                break;
            default: throw new ChangeValidationException("Неизвестный тип команды.");
        }
        if (result.Operations.Count == 0 || result.Operations.Count > MaxOperations) throw new ChangeValidationException("Недопустимое количество операций.");
        return result;
    }
    private static void EnsureFullHistory(IReadOnlyList<StoredAction> history, long head, Guid ticket)
    {
        if ((long)history.Count != head || history.Any(x => x.TicketId != ticket || x.SchemaVersion != 1)) throw new ChangeValidationException("Требуется полная совместимая история обращений.");
        long sequence = 0;
        foreach (var action in history.OrderBy(x => x.Sequence))
            if (action.Sequence != ++sequence) throw new ChangeValidationException("Разрыв в последовательности событий.");
    }
}