# windows-hosts-writer
Small tool that monitors the Docker engine and modifies the hosts file on Windows to allow easier networking

You can run this natively as well but as you need to have Docker running anyways to use it, the easiest way is:

`docker run -v \\.\pipe\docker_engine:\\.\pipe\docker_engine -v c:\Windows\System32\drivers\etc:c:\driversetc tobiasfenster/windows-hosts-writer:1809`

If something breaks or you want to see a bit more about what is actually happening, add `-e debug=true`

PLEASE NOTE: As you can see this allows the container access to a sensitive part of your Windows environment