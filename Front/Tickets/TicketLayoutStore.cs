using Microsoft.JSInterop;
using System.Text.Json;
using UniversalTables.Models;
using UniversalTables.Services;

namespace Front.Tickets;

public sealed class TicketLayoutStore(IJSRuntime js) : ITableLayoutStore
{
    private static string Key(string type, Guid user) => $"TicketEditor:layout:{user:D}:{type}";

    public async Task<LayoutSettings> LoadAsync(string settingsType, Guid userId, CancellationToken cancellationToken)
    {
        var json = await js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, Key(settingsType, userId));
        return new([], json is null ? [] : JsonSerializer.Deserialize<ColumnLayout[]>(json) ?? []);
    }

    public Task SaveUserAsync(string settingsType, Guid userId, IReadOnlyList<ColumnLayout> columns, CancellationToken cancellationToken) =>
        js.InvokeVoidAsync("localStorage.setItem", cancellationToken, Key(settingsType, userId), JsonSerializer.Serialize(columns)).AsTask();
}