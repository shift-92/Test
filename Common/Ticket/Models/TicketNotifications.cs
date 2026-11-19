namespace Common.Ticket.Models;

public static class TicketNotifications
{
    public const string Path = "hubs/tickets";
    public const string Subscribe = "Subscribe";
    public const string Changed = "TicketChanged";
}

public enum EnumTicketStatus
{
    Create,
    Project,
    Open,
    InWork,
    Complited,
    NotComplited
}