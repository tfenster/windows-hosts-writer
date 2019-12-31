FROM mcr.microsoft.com/dotnet/core/runtime:3.1

COPY  ./app ./app
WORKDIR /app
USER ContainerAdministrator
ENTRYPOINT ["dotnet", "windows-hosts-writer.dll"]