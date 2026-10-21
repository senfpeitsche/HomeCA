# HomeCA container image. TLS is terminated by a reverse proxy in front of the
# container; the in-app TLS activation relies on systemd and is not available here.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/HomeCA.Service/HomeCA.Service.csproj src/HomeCA.Service/
RUN dotnet restore src/HomeCA.Service/HomeCA.Service.csproj
COPY profiles/ profiles/
COPY src/ src/
RUN dotnet publish src/HomeCA.Service/HomeCA.Service.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# ssh-keygen signs SSH certificates.
RUN apt-get update \
    && apt-get install -y --no-install-recommends openssh-client \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY deploy/docker/entrypoint.sh /usr/local/bin/homeca-entrypoint
# A Windows checkout may add CR line endings, which would break the shebang.
RUN sed -i 's/\r$//' /usr/local/bin/homeca-entrypoint \
    && chmod 755 /usr/local/bin/homeca-entrypoint \
    && install -d -o app -g app -m 700 /data /backups /config
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:5080 \
    Storage__RootPath=/data \
    Storage__BackupPath=/backups \
    Storage__BackupKeyPath=/config/backup.key \
    Storage__CaKeyPath=/config/ca.key \
    Storage__ConfigurationPath=/config
USER app
VOLUME ["/data", "/backups", "/config"]
EXPOSE 5080
ENTRYPOINT ["/usr/local/bin/homeca-entrypoint"]
