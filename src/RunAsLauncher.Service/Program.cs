using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RunAsLauncher;

// Argumentos de manutenção (usados pelo instalador; exigem admin).
if (args.Length == 1)
{
    switch (args[0])
    {
        case "--register-eventsource":
            AuditLog.RegisterSource();
            return 0;
        case "--unregister-eventsource":
            AuditLog.UnregisterSource();
            return 0;
    }
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "RunAsLauncher");

builder.Services.AddSingleton<AuditLog>();
builder.Services.AddSingleton<PolicyProvider>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PolicyProvider>());
builder.Services.AddSingleton<LaunchService>();
builder.Services.AddHostedService<PipeServer>();

await builder.Build().RunAsync();
return 0;
