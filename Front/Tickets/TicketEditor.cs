using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Ticket.Engine;
using Common.Ticket.Models;
using Common.Ticket.Services;
using UniversalTables.Models;
using UniversalTables.Services;

namespace Front.Tickets;

public sealed class TicketEditor(IHttpClientFactory clients, TableModelRegistry registry, Components.Shared.ChangesViewService views, WebDataService cache, EditorObjectFactory factory, IServiceProvider services) : ICommandTransport
{
    public TicketSession? Session { get; private set; }
    public event Action<CommandCompletion>? Completed;
    private sealed record Created(Guid Id);
    private sealed record ActiveTicket(Guid? Id);

    public async Task<Guid?> FindActiveAsync(Guid itSystemId, CancellationToken ct)
    {
        using var http = clients.CreateClient("TicketApi");
        var result = await http.GetFromJsonAsync<ActiveTicket>($"api/ticket/system/{itSystemId:D}/active", ct);
        return (result ?? throw new InvalidDataException("Сервер не вернул результат поиска заявки.")).Id;
    }

    public async Task<Guid> CreateAsync(Guid itSystemId, CancellationToken ct)
    {
        using var http = clients.CreateClient("TicketApi");
        using var response = await http.PostAsJsonAsync("api/ticket", new { itSystemId }, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("ИТ-система не найдена.");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Created>(ct))?.Id
            ?? throw new InvalidDataException("Сервер не вернул идентификатор заявки.");
    }

    private async Task<TicketSnapshot> ReadAsync(Guid id, CancellationToken ct)
    {
        using var http = clients.CreateClient("TicketApi");
        using var response = await http.GetAsync($"api/ticket/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound) throw new InvalidOperationException("Заявка не найдена.");
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadFromJsonAsync<TicketSnapshot>(ChangeJson.Options, ct);
        return data?.Id == id ? data : throw new InvalidDataException("Некорректный снимок заявки.");
    }

    public async Task RefreshAsync(TicketSession session, CancellationToken ct)
    {
        var snapshot = await ReadAsync(session.Id, ct);
        if (!ct.IsCancellationRequested && ReferenceEquals(Session, session)) session.Update(snapshot);
    }

    public async Task<TicketSession> OpenAsync(Guid itSystemId, Guid id, CancellationToken ct)
    {
        var snapshot = await ReadAsync(id, ct);
        ct.ThrowIfCancellationRequested();
        if (!snapshot.OriginalRows.Any(row => row.Key.Type == "itSystem" && row.Key.Id == itSystemId))
            throw new InvalidOperationException("Заявка не принадлежит указанной ИТ-системе.");
        return Session = new(snapshot, registry, views, cache);
    }

    public async Task<TransportReceipt> SendAsync(CommandEnvelope envelope, CancellationToken ct)
    {
        var session = Session;
        if (session is null || !ReferenceEquals(envelope.Context, session.Data.Context))
            return new(TransportStatus.Rejected, "Заявка уже закрыта.");
        var edit = envelope.Request.Deserialize<EditRequest>(TableJson.Options);
        if (edit is null || !TicketEditPolicy.ByType.TryGetValue(edit.Root.ObjectType, out var policy) ||
            edit.Collections.Count != 0 || edit.Fields.Any(f => f.Path.Count != 1 || f.Path[0].ElementId is not null) ||
            (edit.FixedValues.Count != 0 && edit.Kind is not (ChangeKind.Add or ChangeKind.Copy)))
            return new(TransportStatus.Rejected, "Нельзя применить эту правку к заявке.");
        var key = new Common.Ticket.Models.EntityKey(edit.Root.ObjectType, edit.Root.Id);
        var columns = registry.Get(key.Type).CreateColumns(services);
        foreach (var field in edit.Fields)
        {
            if (!columns.Any(c => c.Id == field.ColumnId && c.Collection is null && !c.OneToOne &&
                (c.Reference?.ForeignKeyPath ?? c.PropertyPath) == field.Path[0].Property && field.Path[0].Property != policy.SystemField &&
                ((c.FormVisible && (c.FormEditable ?? c.CellEditable)) || (edit.Kind == ChangeKind.Edit && c.CellEditable))))
                return new(TransportStatus.Rejected, $"Недоступное поле: {field.Path[0].Property}.");
        }
        List<PatchOperation> operations = [];
        List<EntityKey> entities = [];
        var expectedSequence = session.Sequence;
        string kind = "patch", title;
        switch (edit.Kind)
        {
            case ChangeKind.Edit when session.Data.CanEdit(edit.Root):
                if (edit.Fields.Count == 0)
                {
                    Completed?.Invoke(new(envelope.ActionId, CompletionStatus.NotChanged));
                    return new(TransportStatus.Accepted);
                }
                title = $"Изменение {key.Type}: {string.Join(", ", edit.Fields.Select(f => f.ColumnId))}";
                operations.AddRange(edit.Fields.Select(f => Patch.Set(key, f.Path[0].Property, f.Value)));
                break;
            case ChangeKind.Add:
            case ChangeKind.Copy:
                try
                {
                    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                    if (edit.Kind == ChangeKind.Copy)
                    {
                        EntityState? defaults = null;
                        foreach (var column in columns.Where(c => !c.CopyValue && c.Collection is null && !c.OneToOne))
                        {
                            var property = column.Reference?.ForeignKeyPath ?? column.PropertyPath;
                            defaults ??= factory.Create(key.Type).Rows.Single();
                            if (defaults.Properties.TryGetValue(property, out var value)) values[property] = value;
                        }
                    }
                    foreach (var field in edit.Fields) values[field.Path[0].Property] = field.Value;
                    foreach (var (property, value) in edit.FixedValues)
                    {
                        if (property != policy.Parent || !TicketEditPolicy.IsLink(value))
                            return new(TransportStatus.Rejected, "Недопустимая привязка к родителю.");
                        values[property] = value;
                    }
                    if (policy.SystemField is not null) values[policy.SystemField] = session.ItSystemId;
                    var addition = edit.Kind == ChangeKind.Add ? factory.Create(key.Type, values, key.Id)
                        : factory.Copy(session.Current, new(key.Type, edit.SourceId ?? throw new ChangeValidationException("Не указан источник копии.")), rootValues: values, rootId: key.Id);
                    policy.Validate(addition.Rows.Single(r => r.Key == addition.Root));
                    key = addition.Root;
                    operations = addition.ToOperations();
                }
                catch (ChangeValidationException ex) { return new(TransportStatus.Rejected, ex.Message); }
                title = edit.Kind == ChangeKind.Copy ? $"Копирование {key.Type}" : $"Добавление {key.Type}";
                break;
            case ChangeKind.Delete when session.Data.CanEdit(edit.Root):
                kind = "delete";
                title = $"Удаление {key.Type}";
                break;
            case ChangeKind.DeleteMany when edit.SelectedRoots is { Count: > 0 } roots && roots.Contains(edit.Root) &&
                roots.All(k => k.ObjectType == key.Type && session.Data.CanEdit(k)):
                if (edit.PreviewVersion != expectedSequence.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    return new(TransportStatus.Rejected, "Заявка изменилась. Проверьте план удаления ещё раз.");
                kind = "deleteMany";
                entities = [.. roots.Select(k => new EntityKey(k.ObjectType, k.Id)).Distinct()];
                title = $"Удаление выделенных {key.Type}: {entities.Count}";
                break;
            case ChangeKind.Restore when session.Data.CanRestore(edit.Root):
                kind = "restore";
                title = $"Восстановление {key.Type}";
                break;
            default:
                return new(TransportStatus.Rejected, "Операция недоступна для этой строки.");
        }
        var command = new ChangeCommand
        {
            Kind = kind, TicketId = session.Id, ActionId = envelope.ActionId, ExpectedSequence = expectedSequence,
            Title = title, Entity = key, Entities = entities, Operations = operations
        };
        using var http = clients.CreateClient("TicketApi");
        using var response = await http.PostAsJsonAsync($"api/ticket/{session.Id}/commands", command, ChangeJson.Options, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var fresh = await ReadAsync(session.Id, ct);
            session.Update(fresh);
            if (!fresh.Actions.Any(a => a.ActionId == envelope.ActionId))
                return new(TransportStatus.Rejected, "Заявка изменилась. Загружена новая версия; операция не сохранена. Проверьте актуальные данные.");
        }
        else
        {
            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var error = await response.Content.ReadFromJsonAsync<ChangeErrorDto>(ChangeJson.Options, ct);
                return new(TransportStatus.Rejected, error?.Message ?? "Сервер отклонил команду.");
            }
            if (response.StatusCode == HttpStatusCode.NotFound) return new(TransportStatus.Rejected, "Заявка не найдена.");
            response.EnsureSuccessStatusCode();
            var action = await response.Content.ReadFromJsonAsync<StoredAction>(ChangeJson.Options, ct);
            if (action?.ActionId != envelope.ActionId || action.TicketId != session.Id)
                return new(TransportStatus.Unknown, "Не удалось сопоставить подтверждение с командой.");
            session.Update(await ReadAsync(session.Id, ct));
        }
        Completed?.Invoke(new(envelope.ActionId, CompletionStatus.Confirmed));
        return new(TransportStatus.Accepted);
    }

    public async Task<CommandCompletion?> GetOutcomeAsync(CommandEnvelope envelope, CancellationToken ct)
    {
        if (envelope.Context.TicketId is not Guid id) return null;
        var snapshot = await ReadAsync(id, ct);
        if (Session?.Id == id) Session.Update(snapshot);
        return snapshot.Actions.Any(a => a.ActionId == envelope.ActionId)
            ? new(envelope.ActionId, CompletionStatus.Confirmed) : null;
    }
}