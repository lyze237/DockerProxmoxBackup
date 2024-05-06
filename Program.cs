using DockerProxmoxBackup.Jobs;
using DockerProxmoxBackup.Options;
using Quartz;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ProxmoxOptions>(builder.Configuration.GetSection("Proxmox"));

builder.Services.AddQuartz(q =>
{
    var job = new JobKey("backup");
    q.AddJob<BackupJob>(opts => opts.WithIdentity(job));

    q.AddTrigger(opts =>
    {
        var cronjob = builder.Configuration.GetSection("Proxmox")["Cronjob"] ??
                      throw new ArgumentNullException("Proxmox__Cronjob environment variable not set");
        Console.WriteLine($"Running backups with cronjob: {cronjob}");

        opts.ForJob(job)
            .WithIdentity("backup-trigger")
            .WithCronSchedule(cronjob)
            .StartNow();
    });
});

builder.Services.AddQuartzHostedService(opts => { opts.WaitForJobsToComplete = true; });

var host = builder.Build();
host.Run();
