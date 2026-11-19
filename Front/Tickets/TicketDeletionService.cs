using System.Text.Json;
using Common.Ticket.Engine;
using Common.Ticket.Models;
using UniversalTables.Models;
using UniversalTables.Services;
using TableKey = UniversalTables.Models.EntityKey;

namespace Front.Tickets;

public sealed class TicketDeletionService(TicketEditor editor, ModelRegistry registry, Common.Ticket.Metadata.ModelRegistry schema,
    CommandPlanner planner, HistoryEngine engine) : IDeletionService
{
    public Task<DeletePreview> PreviewDeleteAsync(TableKey root, CancellationToken ct) => PreviewAsync(root, false, ct);
    public Task<DeletePreview> PreviewRestoreAsync(TableKey root, CancellationToken ct) => PreviewAsync(root, true, ct);

    private Task<DeletePreview> PreviewAsync(TableKey root, bool restore, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var session = editor.Session ?? throw new InvalidOperationException("Заявка уже закрыта.");
        var command = new ChangeCommand { TicketId = session.Id, ActionId = Guid.NewGuid(), ExpectedSequence = session.Sequence,
            Entity = new Common.Ticket.Models.EntityKey(root.ObjectType, root.Id), Kind = restore ? "restore" : "delete", Title = "Предпросмотр" };
        try
        {
            var plan = planner.Plan(command, session.Original, session.Current, session.Actions, session.Sequence);
            engine.Apply(session.Current, plan.Operations); // Проверка на копии; сессия не изменяется.
            var affected = plan.Operations.Select(op => Patch.Parse(op.Path).Key).Distinct().ToArray();
            var dependencies = affected.SelectMany(key => schema.Get(key.Type).References
                .Where(r => r.TargetType == root.ObjectType && session.Current.Entities[key].Properties[r.Property] is { ValueKind: JsonValueKind.String } fk && fk.GetGuid() == root.Id)
                .Select(r => (key.Type, r.Property))).GroupBy(x => x)
                .Select(g => new DependencyGroup(registry.Get(g.Key.Type).Title, g.Key.Type, g.Key.Property, root.Id, g.Count())).ToArray();
            return Task.FromResult(new DeletePreview(true, null, dependencies, [.. affected.Select(k => new TableKey(k.Type, k.Id))]));
        }
        catch (ChangeValidationException e) { return Task.FromResult(new DeletePreview(false, e.Message, [], [])); }
    }
}