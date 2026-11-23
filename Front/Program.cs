using Front.Components;
using Front.Tickets;
using UniversalTables.Services;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add MudBlazor services
builder.Services.AddMudServices();






builder.Services.AddUniversalTables(TicketTables.Register);
builder.Services.AddUniversalTableForms();
builder.Services.AddHttpClient("TicketApi", http => http.BaseAddress = new Uri(builder.Configuration["TicketApi:BaseUrl"] ?? throw new InvalidOperationException("Не задан TicketApi:BaseUrl.")));
builder.Services.AddScoped<TicketEditor>();
builder.Services.AddSingleton<Front.WebDataService>();
builder.Services.AddSingleton(Common.Ticket.Metadata.ModelRegistry.Scan(typeof(Common.Ticket.Domain.DbItSystem).Assembly));
builder.Services.AddSingleton<Common.Ticket.Services.EditorObjectFactory>();
builder.Services.AddSingleton<Common.Ticket.Engine.HistoryEngine>();
builder.Services.AddSingleton<Common.Ticket.Engine.BulkDeletePlanner>();
builder.Services.AddSingleton<Common.Ticket.Engine.RestorePlanner>();
builder.Services.AddSingleton<Common.Ticket.Engine.CommandPlanner>();
builder.Services.AddSingleton<Front.Components.Shared.ChangesViewService>();
builder.Services.AddScoped<ICommandTransport>(sp => sp.GetRequiredService<TicketEditor>());
builder.Services.AddScoped<ITableLayoutStore, TicketLayoutStore>();
builder.Services.AddScoped<IDeletionService, TicketDeletionService>();





// Add services to the container.
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseHttpsRedirection();


app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
