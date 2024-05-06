using DockerProxmoxBackup;
using DockerProxmoxBackup.Options;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ProxmoxOptions>(builder.Configuration.GetSection("Proxmox"));
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
