# DockerProxmoxBackup
A simple tool to backup docker containers and postgres containers to a proxmox backup server

---

Add the backup container to either a docker compose or docker stack (for docker swarm) file:

```yml
services:
    DockerProxmoxBackup:
        # swap this if you're using swarm
        # hostname: "{{.Node.Hostname}}"
        hostname: devtools
        image: ghcr.io/lyze237/dockerproxmoxbackup:main
        secrets:
            - proxmox_password_secret
        volumes:
            - /var/run/docker.sock:/var/run/docker.sock:ro
            - /var/lib/docker/volumes:/mnt/var/lib/docker/volumes:ro
        environment:
            Proxmox__PasswordFile: "/run/secrets/proxmox_password_secret"
            Proxmox__Repository: "bdd@pbs@backups.chirps.cafe:backups"
            Proxmox__Namespace: "files"
            Proxmox__Cronjob: "0 0 3 * * ?" # https://www.quartz-scheduler.net/documentation/quartz-3.x/tutorial/crontrigger.html
            Proxmox__CronitorUrl: https://cronitor.link/p/...
```

The tool uses the following order for the backup file name:
* Label: `backup.name`
* Label: `com.docker.swarm.service.name`
* Hostname
* ID

```yml
    OtherPostgresExample:
        image: postgres
        labels:
            backup.name: other
            backup.postgres_user: test
        environment:
            POSTGRES_USER: test
            POSTGRES_PASSWORD: password
```

Additionally it picks up the postgres username the following way:
* Label `backup.postgres_user`
* Environment variable `POSTGRES_USER` on the database container
* `postgres` user

To skip a container set the label `backup.skip=true`

Try and keep the name to the same across backups, so that proxmox can deduplicate it.

Here's a full swarm example which deploys the container across all nodes:

```yml
version: '3.8'

services:
    dockerProxmoxBackup:
        image: ghcr.io/lyze237/dockerproxmoxbackup:main
        hostname: "{{.Node.Hostname}}"
        volumes:
            - /var/run/docker.sock:/var/run/docker.sock:ro
            - /var/lib/docker/volumes:/mnt/var/lib/docker/volumes:ro
        secrets:
            - proxmox_password_secret
        environment:
            Proxmox__PasswordFile: "/run/secrets/proxmox_password_secret"
            Proxmox__Repository: "bdd@pbs@backups.chirps.cafe:backups"
            Proxmox__Namespace: "files"
            Proxmox__Cronjob: "0 0 3 * * ?" # https://www.quartz-scheduler.net/documentation/quartz-3.x/tutorial/crontrigger.html
        deploy:
            mode: global

secrets:
    proxmox_password_secret:
        file: proxmox_password.secret
```