docker build -t rahnemann/windows-hosts-writer:2.0-nanoserver-1809 --build-arg BASE_IMAGE=mcr.microsoft.com/dotnet/runtime:6.0.4-nanoserver-1809 .
docker build -t rahnemann/windows-hosts-writer:2.0-nanoserver-1909 --build-arg BASE_IMAGE=mcr.microsoft.com/dotnet/runtime:6.0.4-nanoserver-1909 .
docker build -t rahnemann/windows-hosts-writer:2.0-nanoserver-2004 --build-arg BASE_IMAGE=mcr.microsoft.com/dotnet/runtime:6.0.4-nanoserver-2004 .
docker build -t rahnemann/windows-hosts-writer:2.0-nanoserver-20H2 --build-arg BASE_IMAGE=mcr.microsoft.com/dotnet/runtime:6.0.4-nanoserver-20H2 .