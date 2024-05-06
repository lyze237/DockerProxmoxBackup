using System.Diagnostics;
using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerProxmoxBackup.Options;
using Microsoft.Extensions.Options;
using Quartz;

namespace DockerProxmoxBackup.Jobs;

public class BackupJob(
    IOptions<ProxmoxOptions> proxmoxOptions,
    ILogger<BackupJob> logger) : IJob
{
    private readonly ProxmoxOptions proxmoxOptions = proxmoxOptions.Value;

    private readonly DockerClient client = new DockerClientConfiguration().CreateClient();

    public async Task Execute(IJobExecutionContext context)
    {
        logger.LogInformation("Running worker");

        var containers =
            await client.Containers.ListContainersAsync(new ContainersListParameters(), context.CancellationToken);

        var directory = new DirectoryInfo($"/tmp/{DateTime.Now.ToString("O")}");

        foreach (var container in containers)
        {
            logger.LogDebug("Checking {Container}", container.ID);
            if (!container.Image.Contains("postgres"))
                continue;

            logger.LogInformation("Backing up {Container} {Hostnames}", container.ID,
                string.Join(", ", container.Names));
            var createDumpResult = await DumpToFile(container, context.CancellationToken);
            logger.LogInformation("Backed up to {File} inside container with exit code {ExitCode}",
                createDumpResult.fileName, createDumpResult.exitCode);

            logger.LogDebug(createDumpResult.stdout.Trim());
            logger.LogError(createDumpResult.stderr.Trim());

            if (createDumpResult.exitCode != 0)
            {
                logger.LogError("Failed to backup {Container} due to exit code {ExitCode} != 0, terminating",
                    container.ID, createDumpResult.exitCode);

                continue;
            }

            logger.LogInformation("Extracting dump from container");
            var getDumpFileResult =
                await CpDumpFromContainer(container, createDumpResult.fileName, context.CancellationToken);

            var backupFile = await WriteDumpToFile(directory, container, getDumpFileResult, context.CancellationToken);

            logger.LogInformation("Added dump {file} to proxmox backup with {Size} <size units>", backupFile.FullName,
                getDumpFileResult.Stat.Size);
        }

        await UploadToProxmox(directory);
        directory.Delete(true);
    }


    private async Task<FileInfo> WriteDumpToFile(DirectoryInfo directory, ContainerListResponse container,
        GetArchiveFromContainerResponse dumpFile, CancellationToken stoppingToken)
    {
        if (!directory.Exists)
            directory.Create();

        var tmpFile = new FileInfo(Path.Combine(directory.FullName, $"{GetContainerName(container)}.dump"));
        await using var tmpStream = File.Create(tmpFile.FullName);
        await dumpFile.Stream.CopyToAsync(tmpStream, stoppingToken);

        return tmpFile;
    }

    private async Task UploadToProxmox(DirectoryInfo directory)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("proxmox-backup-client")
            {
                ArgumentList =
                {
                    "backup", $"dockerProxmoxBackup.pxar:{directory.FullName}", "--repository",
                    proxmoxOptions.Repository,
                    "--ns", proxmoxOptions.Namespace
                },
                Environment = { { "PBS_PASSWORD_FILE", proxmoxOptions.PasswordFile } },
                RedirectStandardError = true,
                RedirectStandardOutput = true
            }
        };

        logger.LogInformation("Running proxmox-backup-client {Args} with Env Args {Env}",
            string.Join(" ", process.StartInfo.ArgumentList),
            string.Join(", ", process.StartInfo.Environment.Select(kvp => $"{kvp.Key}={kvp.Value}")));

        process.OutputDataReceived += (_, args) => logger.LogInformation($"[Exec] {args.Data}");
        process.ErrorDataReceived += (_, args) => logger.LogInformation($"[Exec] {args.Data}");
        process.Start();
        await process.WaitForExitAsync();

        logger.LogInformation("Proxmox backup exited with {ExitCode}", process.ExitCode);

        if (process.ExitCode != 0)
            logger.LogError("Proxmox exited in a bad state.");
    }

    private async Task<GetArchiveFromContainerResponse> CpDumpFromContainer(ContainerListResponse container,
        string fileName, CancellationToken stoppingToken)
    {
        var getArchiveFromContainerParameters = new GetArchiveFromContainerParameters
        {
            Path = fileName
        };

        var getDumpCommand = await client.Containers.GetArchiveFromContainerAsync(container.ID,
            getArchiveFromContainerParameters, false, stoppingToken);
        return getDumpCommand;
    }

    private async Task<(string stdout, string stderr, long exitCode, string fileName)> DumpToFile(
        ContainerListResponse container, CancellationToken stoppingToken)
    {
        var fileName = "/postgres.dump";

        var cmd = new[] { "pg_dumpall", "--clean", "-U", await GetPostgresUsername(container), "-f", fileName };
        logger.LogInformation("Dumping with command {Command}", string.Join(" ", cmd));
        
        var result = await client.Exec.ExecCreateContainerAsync(container.ID, new ContainerExecCreateParameters
        {
            AttachStderr = true,
            AttachStdout = true,
            Cmd = cmd
        }, stoppingToken);

        using var stream = await client.Exec.StartAndAttachContainerExecAsync(result.ID, true, stoppingToken);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(stoppingToken);
        var execInspectResponse = await client.Exec.InspectContainerExecAsync(result.ID, stoppingToken);

        return (stdout, stderr, execInspectResponse.ExitCode, fileName);
    }

    private string GetContainerName(ContainerListResponse container)
    {
        if (container.Labels.TryGetValue("backup.name", out var labelName))
            return labelName;

        if (container.Labels.TryGetValue("com.docker.swarm.service.name", out var sarmName))
            return sarmName;

        return container.Names.FirstOrDefault()?.Replace("/", "") ?? container.ID;
    }

    private async Task<string> GetPostgresUsername(ContainerListResponse container)
    {
        if (container.Labels.TryGetValue("backup.postgres_user", out var postgresUser))
            return postgresUser;

        var inspect = await client.Containers.InspectContainerAsync(container.ID);
        var user = inspect.Config.Env.FirstOrDefault(env => env.StartsWith("POSTGRES_USER"));

        return user?.Split("=")[1] ?? "postgres";
    }
}