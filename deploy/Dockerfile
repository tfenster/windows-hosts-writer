#escape=`
ARG BASE_IMAGE
FROM $BASE_IMAGE

COPY  ./app ./app
WORKDIR /app
USER ContainerAdministrator
ENTRYPOINT ["dotnet", "windows-hosts-writer.dll"]