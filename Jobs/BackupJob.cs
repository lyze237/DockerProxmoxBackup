using System.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerProxmoxBackup.Options;
using Microsoft.Extensions.Options;
using Quartz;
using DockerProxmoxBackup.Extensions;

namespace DockerProxmoxBackup.Jobs;

public class BackupJob(
    IOptions<ProxmoxOptions> proxmoxOptions,
    ILogger<BackupJob> logger) : BackgroundService, IJob
{
    private readonly ProxmoxOptions proxmoxOptions = proxmoxOptions.Value;

    private readonly DockerClient client = new DockerClientConfiguration().CreateClient();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) =>
        await Run(stoppingToken);

    public async Task Execute(IJobExecutionContext context) =>
        await Run(context.CancellationToken);

    private async Task Run(CancellationToken stoppingToken)
    {
        logger.LogInformation("Running worker");

        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters(), stoppingToken);

        var postgresDirectory = await DoPostgresBackups(containers, stoppingToken);
        var mountsDirectories = await DoContainerBackups(containers, stoppingToken);

        var allDirectories = new List<(string, string)> { postgresDirectory };
        allDirectories.AddRange(mountsDirectories);

        logger.LogInformation("Uploading {Amount} Folders", allDirectories.Count);
        await UploadToProxmox(allDirectories.ToArray());

        if (Directory.Exists(postgresDirectory.directory))
            Directory.Delete(postgresDirectory.directory, true);
    }

    private async Task<List<(string, string)>> DoContainerBackups(IList<ContainerListResponse> containers,
        CancellationToken stoppingToken)
    {
        var mounts = new List<(string, string)>();

        foreach (var container in containers)
        {
            logger.LogDebug("Checking {Container}", container.ID);
            if (!container.Image.Contains("postgres") && !container.Image.Contains("pgvecto-rs"))
                mounts.AddRange(await BackupContainer(container, stoppingToken));
        }

        return mounts;
    }

    private async Task<List<(string, string)>> BackupContainer(ContainerListResponse container,
        CancellationToken stoppingToken)
    {
        logger.LogInformation("Backing up mount {Container} {Hostnames}", container.ID,
            string.Join(", ", container.Names));

        var inspect = await client.Containers.InspectContainerAsync(container.ID, stoppingToken);

        var mounts = new List<(string, string)>();

        foreach (var hostMount in inspect.HostConfig.Mounts ?? Array.Empty<Mount>())
        {
            if (!hostMount.Type.Equals("volume"))
                continue;

            // exclude non local mounts
            if (hostMount.VolumeOptions?.DriverConfig?.Options?.ContainsKey("type") ?? false)
                continue;

            var mount = inspect.Mounts.First(m => m.Name == hostMount.Source);
            var path = "/mnt" + mount.Source;

            logger.LogInformation("Backing up volume {Path}", path);
            mounts.Add(($"{container.GetContainerName()}_{mount.Name}", path));
        }

        return mounts;
    }


    private async Task<(string name, string directory)> DoPostgresBackups(IList<ContainerListResponse> containers,
        CancellationToken stoppingToken)
    {
        var directory = new DirectoryInfo($"/tmp/{DateTime.Now.ToString("O")}");

        foreach (var container in containers)
        {
            logger.LogDebug("Checking {Container}", container.ID);
            if (container.Image.Contains("postgres") || container.Image.Contains("pgvecto-rs"))
                await BackupPostgresDb(directory, container, stoppingToken);
        }

        return ("dockerProxmoxBackup", directory.FullName);
    }

    private async Task BackupPostgresDb(DirectoryInfo directory, ContainerListResponse container,
        CancellationToken stoppingToken)
    {
        logger.LogInformation("Backing up postgres {Container} {Hostnames}", container.ID,
            string.Join(", ", container.Names));
        var createDumpResult = await DumpToFile(container, stoppingToken);
        logger.LogInformation("Backed up to {File} inside container with exit code {ExitCode}",
            createDumpResult.fileName, createDumpResult.exitCode);

        logger.LogDebug(createDumpResult.stdout.Trim());
        logger.LogError(createDumpResult.stderr.Trim());

        if (createDumpResult.exitCode != 0)
        {
            logger.LogError("Failed to backup {Container} due to exit code {ExitCode} != 0, terminating", container.ID,
                createDumpResult.exitCode);

            return;
        }

        logger.LogInformation("Extracting dump from container");
        var getDumpFileResult = await CpDumpFromContainer(container, createDumpResult.fileName, stoppingToken);

        var backupFile = await WriteDumpToFile(directory, container, getDumpFileResult, stoppingToken);

        logger.LogInformation("Added dump {file} to proxmox backup with {Size} <size units>", backupFile.FullName,
            getDumpFileResult.Stat.Size);
    }

    private async Task UploadToProxmox(params (string name, string directory)[] directory)
    {
        var processStartInfo = new ProcessStartInfo("proxmox-backup-client")
        {
            Environment = { { "PBS_PASSWORD_FILE", proxmoxOptions.PasswordFile } },
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        processStartInfo.ArgumentList.Add("backup");

        directory.Select(d => $"{d.name}.pxar:{d.directory}").ToList()
            .ForEach(d => processStartInfo.ArgumentList.Add(d));

        processStartInfo.ArgumentList.Add("--repository");
        processStartInfo.ArgumentList.Add(proxmoxOptions.Repository);
        processStartInfo.ArgumentList.Add("--ns");
        processStartInfo.ArgumentList.Add(proxmoxOptions.Namespace);

        var process = new Process { StartInfo = processStartInfo };

        logger.LogInformation("Running proxmox-backup-client {Args} with Env Args {Env}",
            string.Join(" ", process.StartInfo.ArgumentList),
            string.Join(", ", process.StartInfo.Environment.Select(kvp => $"{kvp.Key}={kvp.Value}")));

        process.OutputDataReceived += (_, args) => logger.LogInformation($"[Exec] {args.Data}");
        process.ErrorDataReceived += (_, args) => logger.LogInformation($"[Exec] {args.Data}");
        process.EnableRaisingEvents = true;
        
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        
        await process.WaitForExitAsync();

        logger.LogInformation("Proxmox backup exited with {ExitCode}", process.ExitCode);

        if (process.ExitCode != 0)
            logger.LogError("Proxmox exited in a bad state.");
    }

    private async Task<FileInfo> WriteDumpToFile(DirectoryInfo directory, ContainerListResponse container,
        GetArchiveFromContainerResponse dumpFile, CancellationToken stoppingToken)
    {
        if (!directory.Exists)
            directory.Create();

        var tmpFile = new FileInfo(Path.Combine(directory.FullName, $"{container.GetContainerName()}.dump"));
        await using var tmpStream = File.Create(tmpFile.FullName);
        await dumpFile.Stream.CopyToAsync(tmpStream, stoppingToken);

        return tmpFile;
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

        var cmd = new[] { "pg_dumpall", "--clean", "-U", await container.GetPostgresUsername(client), "-f", fileName };
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
}