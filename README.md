# DockerProxmoxBackup
A simple tool to backup docker containers[1] to a proxmox backup server

[1] Right now only postgres backup

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
        environment:
            Proxmox__PasswordFile: "/run/secrets/proxmox_password_secret"
            Proxmox__Repository: "bdd@pbs@backups.chirps.cafe:backups"
            Proxmox__Namespace: "files"
            Proxmox__Cronjob: "0 0 3 * * ?" # https://www.quartz-scheduler.net/documentation/quartz-3.x/tutorial/crontrigger.html
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
        environment:
            POSTGRES_PASSWORD: password
```

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