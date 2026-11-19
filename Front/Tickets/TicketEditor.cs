using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Common.Ticket.Engine;
using Common.Ticket.Models;
using UniversalTables.Models;
using UniversalTables.Services;

namespace Front.Tickets;

public sealed class TicketEditor(IHttpClientFactory clients, ModelRegistry registry, Front.Components.Shared.ChangesViewService views, WebDataService cache) : ICommandTransport
{
    public TicketSession? Session { get; private set; }
    public event Action<CommandCompletion>? Completed;
    private sealed record Created(Guid Id);

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

    public async Task<TicketSession> OpenAsync(Guid id, CancellationToken ct)
    {
        var snapshot = await ReadAsync(id, ct);
        ct.ThrowIfCancellationRequested();
        return Session = new(snapshot, registry, views, cache);
    }

    public async Task<TransportReceipt> SendAsync(CommandEnvelope envelope, CancellationToken ct)
    {
        var session = Session;
        if (session is null || !ReferenceEquals(envelope.Context, session.Data.Context))
            return new(TransportStatus.Rejected, "Заявка уже закрыта.");
        var edit = envelope.Request.Deserialize<EditRequest>(TableJson.Options);
        if (edit is null || edit.Root.ObjectType is not ("vm" or "cluster" or "networkRule" or "vmDisk" or "ipAddress") ||
            edit.Collections.Count != 0 || edit.Fields.Any(f => f.Path.Count != 1) ||
            (edit.FixedValues.Count != 0 && edit.Kind is not (ChangeKind.Add or ChangeKind.Copy)))
            return new(TransportStatus.Rejected, "Нельзя применить эту правку к заявке.");
        var key = new Common.Ticket.Models.EntityKey(edit.Root.ObjectType, edit.Root.Id);
        List<PatchOperation> operations = [];
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
                var row = new EntityState { Key = key };
                var props = row.Properties;
                var labelField = key.Type == "ipAddress" ? "Address" : "Name";
                props[labelField] = ChangeJson.Value("");
                if (key.Type == "vm")
                {
                    props["Date"] = ChangeJson.Value(default(DateTime));
                    props["Description"] = ChangeJson.Value<string?>(null);
                    props["ClusterId"] = ChangeJson.Value<Guid?>(null);
                    props["UsageId"] = ChangeJson.Value<Guid?>(null);
                }
                if (key.Type == "networkRule")
                {
                    props[nameof(RuleRow.IpAddressId)] = ChangeJson.Value(Guid.Empty);
                    props[nameof(RuleRow.TargetAddressId)] = ChangeJson.Value(Guid.Empty);
                }
                if (key.Type == "vmDisk") props["SizeGb"] = ChangeJson.Value(0m);
                foreach (var field in edit.Fields)
                {
                    var property = field.Path[0].Property;
                    if (!props.ContainsKey(property)) return new(TransportStatus.Rejected, $"Недоступное поле: {property}.");
                    props[property] = field.Value;
                }
                var parentField = key.Type switch { "vmDisk" or "ipAddress" => "VmId", "networkRule" => "IpAddressId", "vm" => "ClusterId", _ => null };
                foreach (var (property, value) in edit.FixedValues)
                {
                    if (property != parentField || value.ValueKind != JsonValueKind.String || !value.TryGetGuid(out var parent) || parent == Guid.Empty)
                        return new(TransportStatus.Rejected, "Недопустимая привязка к родителю.");
                    props[property] = value;
                }
                if (props[labelField].ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(props[labelField].GetString()))
                    return new(TransportStatus.Rejected, key.Type == "ipAddress" ? "Укажите IP-адрес." : "Укажите имя объекта.");
                if (key.Type is "vm" or "cluster") props["ItSystemId"] = ChangeJson.Value(session.ItSystemId);
                if (key.Type is ("vmDisk" or "ipAddress") && !props.ContainsKey("VmId"))
                    return new(TransportStatus.Rejected, "Не указана родительская ВМ.");
                if (key.Type == "networkRule" && new[] { nameof(RuleRow.IpAddressId), nameof(RuleRow.TargetAddressId) }
                    .Any(field => props[field].ValueKind != JsonValueKind.String || !props[field].TryGetGuid(out var id) || id == Guid.Empty))
                    return new(TransportStatus.Rejected, "Выберите IP-адрес источника и назначения.");
                operations.Add(Patch.Add(row));
                title = $"Добавление {key.Type}";
                break;
            case ChangeKind.Delete when session.Data.CanEdit(edit.Root):
                kind = "delete";
                title = $"Удаление {key.Type}";
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
            Kind = kind, TicketId = session.Id, ActionId = envelope.ActionId, ExpectedSequence = session.Sequence,
            Title = title, Entity = key, Operations = operations
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