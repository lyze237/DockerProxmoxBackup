using Docker.DotNet;
using Docker.DotNet.Models;

namespace DockerProxmoxBackup.Extensions;

public static class ContainerExtensions
{
    public static string GetContainerName(this ContainerListResponse container)
    {
        if (container.Labels.TryGetValue("backup.name", out var labelName))
            return labelName;

        if (container.Labels.TryGetValue("com.docker.swarm.service.name", out var sarmName))
            return sarmName;

        return container.Names.FirstOrDefault()?.Replace("/", "") ?? container.ID;
    }

    public static async Task<string> GetPostgresUsername(this ContainerListResponse container, DockerClient client)
    {
        if (container.Labels.TryGetValue("backup.postgres_user", out var postgresUser))
            return postgresUser;

        var inspect = await client.Containers.InspectContainerAsync(container.ID);
        var user = inspect.Config.Env.FirstOrDefault(env => env.StartsWith("POSTGRES_USER"));

        return user?.Split("=")[1] ?? "postgres";
    }
}