# windows-hosts-writer

### Updated: 5-27-20
- Added ability to terminate containers to a single container such as traefik.  

Environment variable name: "TERMINATION_MAP" 
Value: server1,server2:server3 

(The IP of Server 3 will be used for both server 1 and 2)

### Updated: 5-26-20
- No longer need to pass in environment variable for the network. If you don't, it'll list to all networks by default.

### Updated: 1-2-20
- Added arg for the base image to support other OS Versions

### Updated: 1-1-20
- Allows for per-session running
- Upgraded .net core to 3.1
- Switched from events to timered updates to work better with docker-compose

### Updated: 12-17-19
- Allows for configuration of the network to monitor.  Set the "network" environment variable
- Adds the docker compose service name (for better user experience)


Small tool that monitors the Docker engine and modifies the hosts file on Windows to allow easier networking

You can run this natively as well but as you need to have Docker running anyways to use it, the easiest way is:

```docker run -v \\.\pipe\docker_engine:\\.\pipe\docker_engine -v c:\Windows\System32\drivers\etc:c:\driversetc tobiasfenster/windows-hosts-writer:1809```

If something breaks or you want to see a bit more about what is actually happening, add `-e debug=true`

PLEASE NOTE: As you can see this allows the container access to a sensitive part of your Windows environment

In order to test it, run a second container and try to ping it by name, e.g.
```
C:\WINDOWS\system32>docker run --hostname testme -d mcr.microsoft.com/windows/nanoserver:1809 ping -t localhost
d2d4a65cbcb33fad2a11d51c2c75f00ec9883815b364813056d566f6990ca83b

C:\WINDOWS\system32>ping testme

Ping wird ausgeführt für testme [172.26.1.117] mit 32 Bytes Daten:
Antwort von 172.26.1.117: Bytes=32 Zeit=2ms TTL=128
Antwort von 172.26.1.117: Bytes=32 Zeit=3ms TTL=128

Ping-Statistik für 172.26.1.117:
    Pakete: Gesendet = 2, Empfangen = 2, Verloren = 0
    (0% Verlust),
Ca. Zeitangaben in Millisek.:
    Minimum = 2ms, Maximum = 3ms, Mittelwert = 2ms
```
