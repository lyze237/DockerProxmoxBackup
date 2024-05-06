using System.Diagnostics;
using Docker.DotNet;
using Docker.DotNet.Models;
using DockerProxmoxBackup.Options;
using Microsoft.Extensions.Options;
using Quartz;
using DockerProxmoxBackup.Extensions;
using System.Net;

namespace DockerProxmoxBackup.Jobs;

public class BackupJob(
    IOptions<ProxmoxOptions> proxmoxOptions,
    ILogger<BackupJob> logger) : BackgroundService, IJob
{
    private readonly ProxmoxOptions proxmoxOptions = proxmoxOptions.Value;

    private readonly DockerClient dockerClient = new DockerClientConfiguration().CreateClient();

    private readonly HttpClient httpClient = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) =>
        await Run(stoppingToken);

    public async Task Execute(IJobExecutionContext context) =>
        await Run(context.CancellationToken);

    private async Task Run(CancellationToken stoppingToken)
    {
        logger.LogInformation("Running worker");
        await httpClient.GetAsync($"{proxmoxOptions.CronitorUrl}?state=run&host={Dns.GetHostName()}", stoppingToken);

        var containers = await dockerClient.Containers.ListContainersAsync(new ContainersListParameters(), stoppingToken);

        var errorCounts = 0;

        var postgresDirectory = await DoPostgresBackups(containers, stoppingToken);
        errorCounts += postgresDirectory.errorCount;

        var mountsDirectories = await DoContainerBackups(containers, stoppingToken);

        var allDirectories = new List<(string, string)>();
        if (Directory.GetFiles(postgresDirectory.directory).Length > 0)
            allDirectories.Add((postgresDirectory.name, postgresDirectory.directory));
        
        allDirectories.AddRange(mountsDirectories);

        logger.LogInformation("Uploading {Amount} Folders", allDirectories.Count);
        var uploadExitCode = await UploadToProxmox(allDirectories.ToArray());

        await httpClient.GetAsync($"{proxmoxOptions.CronitorUrl}?state={(errorCounts > 0 ? "fail" : "complete")}&host={Dns.GetHostName()}&metric=error_count:{errorCounts}&status_code={uploadExitCode}", stoppingToken);
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

        var inspect = await dockerClient.Containers.InspectContainerAsync(container.ID, stoppingToken);

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


    private async Task<(string name, string directory, int errorCount)> DoPostgresBackups(IList<ContainerListResponse> containers,
        CancellationToken stoppingToken)
    {
        var directory = new DirectoryInfo($"/tmp/{Guid.NewGuid().ToString()}");
        logger.LogInformation($"Putting db stuff into {directory.FullName}");
        directory.Create();

        var errorCount = 0;

        foreach (var container in containers)
        {
            logger.LogDebug("Checking {Container}", container.ID);
            if (container.Image.Contains("postgres") || container.Image.Contains("pgvecto-rs"))
                if (!await BackupPostgresDb(directory, container, stoppingToken))
                    errorCount++;
        }

        return ("dockerProxmoxBackup", directory.FullName, errorCount);
    }

    private async Task<bool> BackupPostgresDb(DirectoryInfo directory, ContainerListResponse container,
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

            return false;
        }

        logger.LogInformation("Extracting dump from container");
        var getDumpFileResult = await CpDumpFromContainer(container, createDumpResult.fileName, stoppingToken);

        var backupFile = await WriteDumpToFile(directory, container, getDumpFileResult, stoppingToken);

        logger.LogInformation("Added dump {file} to proxmox backup with {Size} <size units>", backupFile.FullName,
            getDumpFileResult.Stat.Size);

        return true;
    }

    private async Task<long> UploadToProxmox(params (string name, string directory)[] directory)
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

        return process.ExitCode;
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

        var getDumpCommand = await dockerClient.Containers.GetArchiveFromContainerAsync(container.ID,
            getArchiveFromContainerParameters, false, stoppingToken);
        return getDumpCommand;
    }

    private async Task<(string stdout, string stderr, long exitCode, string fileName)> DumpToFile(
        ContainerListResponse container, CancellationToken stoppingToken)
    {
        var fileName = "/postgres.dump";

        var cmd = new[] { "pg_dumpall", "--clean", "-U", await container.GetPostgresUsername(dockerClient), "-f", fileName };
        logger.LogInformation("Dumping with command {Command}", string.Join(" ", cmd));

        var result = await dockerClient.Exec.ExecCreateContainerAsync(container.ID, new ContainerExecCreateParameters
        {
            AttachStderr = true,
            AttachStdout = true,
            Cmd = cmd
        }, stoppingToken);

        using var stream = await dockerClient.Exec.StartAndAttachContainerExecAsync(result.ID, true, stoppingToken);
        var (stdout, stderr) = await stream.ReadOutputToEndAsync(stoppingToken);
        var execInspectResponse = await dockerClient.Exec.InspectContainerExecAsync(result.ID, stoppingToken);

        return (stdout, stderr, execInspectResponse.ExitCode, fileName);
    }
}