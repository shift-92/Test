using System.Text.Json;
using Common.Ticket.Models;

namespace Front.Tickets;

internal sealed record TicketEditPolicy(string Label, string? Parent = null, string? SystemField = null,
    string[]? RequiredLinks = null, string LabelError = "Укажите имя объекта.", string LinkError = "Не указана родительская ВМ.")
{
    internal static readonly IReadOnlyDictionary<string, TicketEditPolicy> ByType = new Dictionary<string, TicketEditPolicy>(StringComparer.Ordinal)
    {
        ["vm"] = new("Name", "ClusterId", "ItSystemId"),
        ["cluster"] = new("Name", SystemField: "ItSystemId"),
        ["vmDisk"] = new("Name", "VmId", RequiredLinks: ["VmId"]),
        ["ipAddress"] = new("Address", "VmId", RequiredLinks: ["VmId"], LabelError: "Укажите IP-адрес."),
        ["networkRule"] = new("Name", nameof(RuleRow.IpAddressId), RequiredLinks: [nameof(RuleRow.IpAddressId), nameof(RuleRow.TargetAddressId)],
            LinkError: "Выберите IP-адрес источника и назначения.")
    };

    internal void Validate(EntityState row)
    {
        if (!row.Properties.TryGetValue(Label, out var label) || label.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(label.GetString()))
            throw new ChangeValidationException(LabelError);
        foreach (var field in RequiredLinks ?? [])
            if (!row.Properties.TryGetValue(field, out var link) || !IsLink(link)) throw new ChangeValidationException(LinkError);
    }

    internal static bool IsLink(JsonElement value) => value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) && id != Guid.Empty;
}