FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
WORKDIR /App

# Copy everything
COPY . ./
# Restore as distinct layers
RUN dotnet restore
# Build and publish a release
RUN dotnet publish -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /App
COPY --from=build-env /App/out .


RUN apt-get update ; apt-get install -y wget
RUN wget https://enterprise.proxmox.com/debian/proxmox-release-bookworm.gpg -O /etc/apt/trusted.gpg.d/proxmox-release-bookworm.gpg
RUN echo deb http://download.proxmox.com/debian/pbs-client bookworm main >> /etc/apt/sources.list.d/pbs-client.list
RUN apt-get update ; apt-get install -y proxmox-backup-client

ENTRYPOINT ["dotnet", "DockerProxmoxBackup.dll"]
