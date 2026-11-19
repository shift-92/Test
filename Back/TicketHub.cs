using Common.Ticket.Models;
using Microsoft.AspNetCore.SignalR;

namespace Back;

public sealed class TicketHub : Hub
{
    [HubMethodName(TicketNotifications.Subscribe)]
    public Task Subscribe(Guid ticketId) => Groups.AddToGroupAsync(Context.ConnectionId, ticketId.ToString("D"));
}