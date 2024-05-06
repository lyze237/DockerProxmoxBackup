namespace DockerProxmoxBackup.Options;

public class ProxmoxOptions
{
    public string PasswordFile { get; set; }
    public string Repository { get; set; }
    public string Namespace { get; set; }
    public string Cronjob { get; set; }

    public string? CronitorUrl { get; set; }
}