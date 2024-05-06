using DockerProxmoxBackup.Jobs;
using DockerProxmoxBackup.Options;
using Quartz;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<ProxmoxOptions>(builder.Configuration.GetSection("Proxmox"));

var cronjob = builder.Configuration.GetSection("Proxmox")["Cronjob"];

if (cronjob != null)
{
    builder.Services.AddQuartz(q =>
    {
        var job = new JobKey("backup");
        q.AddJob<BackupJob>(opts => opts.WithIdentity(job));

        q.AddTrigger(opts =>
        {
            Console.WriteLine($"Running backups with cronjob: {cronjob}");

            opts.ForJob(job)
                .WithIdentity("backup-trigger-2")
                .WithSimpleSchedule(schedule => schedule.WithIntervalInHours(1).Build())
                .StartNow();

            opts.ForJob(job)
                .WithIdentity("backup-trigger")
                .WithCronSchedule(cronjob)
                .StartNow();
        });
    });

    builder.Services.AddQuartzHostedService(opts => { opts.WaitForJobsToComplete = true; });
}

if (cronjob == null)
{
    Console.WriteLine("Proxmox__Cronjob not set, running once right now");
    builder.Services.AddHostedService<BackupJob>();
}

var host = builder.Build();
host.Run();
