#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

#Depending on the operating system of the host machines(s) that will build or run the containers, the image specified in the FROM statement may need to be changed.
#For more information, please see https://aka.ms/containercompat

FROM mcr.microsoft.com/dotnet/runtime:6.0 AS base
USER ContainerAdministrator
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /src
COPY ["windows-hosts-writer.csproj", "."]
RUN dotnet restore "./windows-hosts-writer.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "windows-hosts-writer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "windows-hosts-writer.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:6.0.4-nanoserver-1809 AS final
WORKDIR /app
COPY --from=publish /app/publish .
USER ContainerAdministrator
ENTRYPOINT ["dotnet", "windows-hosts-writer.dll"]