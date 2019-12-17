using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace windows_hosts_writer
{
    class Program
    {
        private const string ENV_ENDPOINT = "endpoint";
        private const string ENV_NETWORK = "network";
        private const string ENV_HOSTPATH = "hosts_path";
        private const string ERROR_HOSTPATH = "could not change hosts file at {0} inside of the container. You can change that path through environment variable " + ENV_HOSTPATH;
        private const string EVENT_MSG = "got a {0} event from {1}";
        private static string LISTEN_NETWORK = "nat";
        private static DockerClient _client;
        private static bool _debug = false;

        static void Main(string[] args)
        {
            if (Environment.GetEnvironmentVariable("debug") != null)
            {
                _debug = true;
                Console.WriteLine("Starting Windows hosts writer");
            }

            if (Environment.GetEnvironmentVariable(ENV_NETWORK) != null)
            {
                LISTEN_NETWORK = Environment.GetEnvironmentVariable(ENV_NETWORK);
            }


            var progress = new Progress<Message>(message =>
            {
                if (message.Action == "connect")
                {
                    if (_debug)
                        Console.WriteLine(EVENT_MSG, "connect", message.Actor.Attributes["container"]);
                    HandleHosts(true, message);
                }
                else if (message.Action == "disconnect")
                {
                    if (_debug)
                        Console.WriteLine(EVENT_MSG, "disconnect", message.Actor.Attributes["container"]);
                    HandleHosts(false, message);
                }
            });

            var containerEventsParams = new ContainerEventsParameters()
            {
                Filters = new Dictionary<string, IDictionary<string, bool>>()
                    {
                        {
                            "event", new Dictionary<string, bool>()
                            {
                                {
                                    "connect", true
                                },
                                {
                                    "disconnect", true
                                }
                            }
                        },
                        {
                            "type", new Dictionary<string, bool>()
                            {
                                {
                                    "network", true
                                }
                            }
                        }
                    }
            };

            try
            {
                GetClient().System.MonitorEventsAsync(containerEventsParams, progress, default(CancellationToken)).Wait();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Something went wrong. Likely the Docker engine is not listening at " + GetClient().Configuration.EndpointBaseUri.ToString() + " inside of the container.");
                Console.WriteLine("You can change that path through environment variable " + ENV_ENDPOINT);
                if (_debug)
                {
                    Console.WriteLine("Exception is " + ex.Message);
                    Console.WriteLine(ex.StackTrace);
                    if (ex.InnerException != null)
                    {
                        Console.WriteLine();
                        Console.WriteLine("InnerException is " + ex.InnerException.Message);
                        Console.WriteLine(ex.InnerException.StackTrace);
                    }
                }

            }
        }

        private static void HandleHosts(bool add, Message message)
        {
            FileStream hostsFileStream = null;
            try
            {

                while (hostsFileStream == null)
                {
                    int tryCount = 0;
                    var hostsPath = Environment.GetEnvironmentVariable(ENV_HOSTPATH) ?? "c:\\driversetc\\hosts";

                    try
                    {
                        hostsFileStream = File.Open(hostsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                    }
                    catch (FileNotFoundException)
                    {
                        // no access to hosts
                        Console.WriteLine(ERROR_HOSTPATH, hostsPath);
                        return;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        // no access to hosts
                        Console.WriteLine(ERROR_HOSTPATH, hostsPath);
                        return;
                    }
                    catch (IOException ex)
                    {
                        if (tryCount == 5)
                        {
                            Console.WriteLine(ERROR_HOSTPATH, hostsPath);
                            return;  // only try five times and then give up
                        }
                        Thread.Sleep(1000);
                        tryCount++;
                    }
                }
                if (message.Actor.Attributes["type"] == "nat")
                {
                    var containerId = message.Actor.Attributes["container"];
                    try
                    {
                        var response = GetClient().Containers.InspectContainerAsync(containerId).Result;
                        var networks = response.NetworkSettings.Networks;
                        EndpointSettings network = null;
                        if (networks.TryGetValue(LISTEN_NETWORK, out network))
                        {
                            var hostsLines = new List<string>();
                            using (StreamReader reader = new StreamReader(hostsFileStream))
                            using (StreamWriter writer = new StreamWriter(hostsFileStream))
                            {
                                while (!reader.EndOfStream)
                                    hostsLines.Add(reader.ReadLine());

                                hostsFileStream.Position = 0;
                                var removed = hostsLines.RemoveAll(l => l.EndsWith($"#{containerId} by whw"));
                                var hostnames = response.Config.Hostname;

                                if (response.Config.Labels.ContainsKey("com.docker.compose.service"))
                                {
                                    hostnames += $" {response.Config.Labels["com.docker.compose.service"]}";
                                }

                                if (add)
                                    hostsLines.Add($"{network.IPAddress}\t{hostnames}\t\t#{containerId} by whw");

                                foreach (var line in hostsLines)
                                    writer.WriteLine(line);
                                hostsFileStream.SetLength(hostsFileStream.Position);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (_debug)
                        {
                            Console.WriteLine("Something went wrong. Maybe looking for a container that is already gone? Exception is " + ex.Message);
                            Console.WriteLine(ex.StackTrace);
                            if (ex.InnerException != null)
                            {
                                Console.WriteLine();
                                Console.WriteLine("InnerException is " + ex.InnerException.Message);
                                Console.WriteLine(ex.InnerException.StackTrace);
                            }
                        }
                    }
                }
            }
            finally
            {
                if (hostsFileStream != null)
                    hostsFileStream.Dispose();
            }
        }

        private static DockerClient GetClient()
        {
            if (_client == null)
            {
                var endpoint = Environment.GetEnvironmentVariable(ENV_ENDPOINT) ?? "npipe://./pipe/docker_engine";
                _client = new DockerClientConfiguration(new System.Uri(endpoint)).CreateClient();
            }
            return _client;
        }
    }
}
