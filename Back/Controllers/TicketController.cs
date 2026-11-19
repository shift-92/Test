using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Common.Ticket.Engine;
using Common.Ticket.Metadata;
using Common.Ticket.Models;
using System.Data;
using System.Security.Claims;
using System.Text.Json;

namespace Back.Controllers;

[Route("api/[controller]")]
[AllowAnonymous]
[ApiController]
public class TicketController(DataContext db, IHubContext<TicketHub> hub, ILogger<TicketController> logger) : ControllerBase
{
    private static readonly ModelRegistry Registry = ModelRegistry.Scan(typeof(DbItSystem).Assembly);
    private static readonly HistoryEngine Engine = new(Registry);
    private static readonly CommandPlanner Planner = new(new BulkDeletePlanner(Registry), new RestorePlanner(Engine));

    public sealed record CreateRequest(Guid ItSystemId);
    public sealed record StatusRequest(EnumTicketStatus? Status);

    private Guid UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    [HttpPost]
    public async Task<IActionResult> Create(CreateRequest request, CancellationToken ct)
    {
        if (request.ItSystemId == Guid.Empty) return BadRequest("Не указан itSystemId.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var system = await db.ItSystems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.ItSystemId, ct);
        if (system is null) return NotFound();

        var vms = db.Vms.AsNoTracking().Where(x => x.ItSystemId == system.Id);
        var vmIds = vms.Select(x => x.Id);
        var addresses = db.IpAddresses.AsNoTracking().Where(x => vmIds.Contains(x.VmId));
        var addressIds = addresses.Select(x => x.Id);
        List<ITicketModel> rows = [system];
        rows.AddRange(await db.Clusters.AsNoTracking().Where(x => x.ItSystemId == system.Id).ToListAsync(ct));
        rows.AddRange(await vms.ToListAsync(ct));
        rows.AddRange(await db.VmDisks.AsNoTracking().Where(x => vmIds.Contains(x.VmId)).ToListAsync(ct));
        rows.AddRange(await addresses.ToListAsync(ct));
        rows.AddRange(await db.NetworkRules.AsNoTracking().Where(x => addressIds.Contains(x.IpAddressId) || addressIds.Contains(x.TargetAddressId)).ToListAsync(ct));

        var state = FlatMapper.Capture(Registry, rows);
        try { Engine.Validate(state); await ValidateExternalAddresses(state, ct); }
        catch (ChangeValidationException e) { return Conflict(new ChangeErrorDto("invalid_snapshot", e.Message)); }

        var snapshot = StateJson.Serialize(state);
        var ticket = new DbTicket { Id = Guid.NewGuid(), OriginalJson = snapshot, CurrentJson = snapshot };
        db.Tickets.Add(ticket);
        RecordStatus(ticket, EnumTicketStatus.Create, request);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = ticket.Id }, new { ticket.Id, ticket.CurrentStatus, ticket.LastSequence });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var ticket = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (ticket is null) return NotFound();
        var actions = await LoadHistory(id, ct);
        var statuses = await db.Set<DbTicketStatus>().Where(x => x.TicketId == id)
            .OrderBy(x => x.DateTimeUtc).ThenBy(x => x.Id)
            .Select(x => new { x.Id, x.Status, x.DateTimeUtc, x.UserId }).ToListAsync(ct);
        await tx.CommitAsync(ct);
        return Ok(new { ticket.Id, ticket.CurrentStatus, ticket.LastSequence, StatusRevision = statuses.Count,
            OriginalRows = StateJson.Deserialize(ticket.OriginalJson).Entities.Values,
            CurrentRows = StateJson.Deserialize(ticket.CurrentJson).Entities.Values, Actions = actions, Statuses = statuses });
    }

    [HttpPost("{id:guid}/commands")]
    public async Task<IActionResult> Submit(Guid id, ChangeCommand command, CancellationToken ct)
    {
        if (command.TicketId != id) return BadRequest("TicketId команды не совпадает с адресом заявки.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockTicket(id, ct);
        if (ticket is null) return NotFound();

        try
        {
            CommandPlanner.ValidateCommand(command);
            if (command.ExpectedSequence != ticket.LastSequence) throw new SequenceConflictException(ticket.LastSequence);
            if (await db.TicketActions.AnyAsync(x => x.Id == command.ActionId, ct))
                return Conflict(new ChangeErrorDto("duplicate_action", "ActionId уже использован.", ticket.LastSequence));
            var current = StateJson.Deserialize(ticket.CurrentJson);
            List<StoredAction> history = command.Kind is "restore" or "restoreMany" or "rollback" ? await LoadHistory(id, ct) : [];
            var plan = Planner.Plan(command, StateJson.Deserialize(ticket.OriginalJson), current, history, ticket.LastSequence);
            var applied = Engine.Apply(current, plan.Operations);
            await ValidateExternalAddresses(applied.State, ct);
            var action = new StoredAction(command.SchemaVersion, command.ActionId, id, ticket.LastSequence + 1,
                DateTimeOffset.UtcNow, command.Title, plan.Operations, applied.InverseOperations,
                UserId == Guid.Empty ? null : UserId.ToString(), User.Identity?.Name, plan.DeletionGroups);
            ticket.CurrentJson = StateJson.Serialize(applied.State);
            ticket.LastSequence = action.Sequence;
            db.TicketActions.Add(new DbTicketAction { Id = action.ActionId, TicketId = id, Ticket = ticket,
                Sequence = action.Sequence, DateTimeUtc = action.CreatedAt.UtcDateTime, UserId = UserId,
                RequestJson = JsonSerializer.Serialize(command, ChangeJson.Options), ActionJson = JsonSerializer.Serialize(action, ChangeJson.Options) });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            await NotifyChangedAsync(id);
            return Ok(action);
        }
        catch (SequenceConflictException e) { return Conflict(new ChangeErrorDto("sequence_conflict", e.Message, e.ActualSequence)); }
        catch (ChangeValidationException e) { return BadRequest(new ChangeErrorDto("invalid_command", e.Message)); }
        catch (JsonException e) { return BadRequest(new ChangeErrorDto("invalid_command", e.Message)); }
    }

    [HttpPut("{id:guid}/status")]
    public async Task<IActionResult> SetStatus(Guid id, StatusRequest request, CancellationToken ct)
    {
        if (request.Status is not { } status || !Enum.IsDefined(status)) return BadRequest("Неизвестный статус заявки.");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var ticket = await LockTicket(id, ct);
        if (ticket is null) return NotFound();
        RecordStatus(ticket, status, request);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await NotifyChangedAsync(id);
        return NoContent();
    }

    private async Task ValidateExternalAddresses(EditorState state, CancellationToken ct)
    {
        var ids = state.Entities.Values.Where(r => !r.IsDeleted && r.Key.Type == "networkRule")
            .SelectMany(r => new[] { r.Properties[nameof(DbNetworkRule.IpAddressId)].GetGuid(), r.Properties[nameof(DbNetworkRule.TargetAddressId)].GetGuid() })
            .Where(id => !state.Entities.ContainsKey(new("ipAddress", id))).Distinct().ToArray();
        foreach (var batch in ids.Chunk(500))
        {
            var found = await db.IpAddresses.Where(a => batch.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct);
            if (found.Count != batch.Length)
                throw new ChangeValidationException($"Внешний IP-адрес {batch.Except(found).First()} не найден в БД.");
        }
    }

    private async Task NotifyChangedAsync(Guid id)
    {
        // Транзакция уже завершена: сбой доставки не должен выглядеть как отказ сохранения.
        try { await hub.Clients.Group(id.ToString("D")).SendAsync(TicketNotifications.Changed); }
        catch (Exception e) { logger.LogWarning(e, "Не удалось уведомить об изменении заявки {TicketId}", id); }
    }

    // Один писатель на заявку: проверка sequence и запись истории выполняются под одной блокировкой SQL Server.
    private Task<DbTicket?> LockTicket(Guid id, CancellationToken ct) => db.Tickets
        .FromSql($"SELECT * FROM [dbo].[tickets] WITH (UPDLOCK) WHERE [Id] = {id}").SingleOrDefaultAsync(ct);

    private async Task<List<StoredAction>> LoadHistory(Guid id, CancellationToken ct)
    {
        var json = await db.TicketActions.Where(x => x.TicketId == id).OrderBy(x => x.Sequence).Select(x => x.ActionJson).ToListAsync(ct);
        return json.Select(x => JsonSerializer.Deserialize<StoredAction>(x, ChangeJson.Options)
            ?? throw new InvalidDataException("Пустая запись истории заявки.")).ToList();
    }

    private void RecordStatus<T>(DbTicket ticket, EnumTicketStatus status, T request)
    {
        ticket.CurrentStatus = status;
        db.Set<DbTicketStatus>().Add(new DbTicketStatus { Id = Guid.NewGuid(), TicketId = ticket.Id, Ticket = ticket,
            Status = status, DateTimeUtc = DateTime.UtcNow, UserId = UserId,
            RequestJson = JsonSerializer.Serialize(request, ChangeJson.Options),
            ActionJson = JsonSerializer.Serialize(new { Status = status }, ChangeJson.Options) });
    }
}