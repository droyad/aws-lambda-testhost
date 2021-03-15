using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Amazon.Runtime;
using Ductus.FluentDocker.Builders;
using Ductus.FluentDocker.Commands;
using Ductus.FluentDocker.Services;
using Ductus.FluentDocker.Services.Impl;
using Microsoft.Extensions.Logging;
using Xunit.Abstractions;

namespace Logicality.AWS.Lambda.TestHost.LocalStack
{
    public class LocalStackFixture : IAsyncDisposable
    {
        private readonly IContainerService _localStack;
        private readonly ITestOutputHelper _outputHelper;
        private const int ContainerPort = 4566;

        public LocalStackFixture(LambdaTestHost lambdaTestHost,
            IContainerService localStack,
            Uri serviceUrl,
            ITestOutputHelper outputHelper)
        {
            ServiceUrl = serviceUrl;
            LambdaTestHost = lambdaTestHost;
            _localStack = localStack;
            _outputHelper = outputHelper;

            AWSCredentials =  new BasicAWSCredentials("not", "used");
        }
        
        public Uri ServiceUrl { get; }

        public LambdaTestHost LambdaTestHost { get; }

        public AWSCredentials AWSCredentials { get; }

        public static async Task<LocalStackFixture> Create(ITestOutputHelper outputHelper)
        {
            // Runs a the Lambda TestHost (invoke api) on a random port
            var settings = new LambdaTestHostSettings(() => new TestLambdaContext())
            {
                ConfigureLogging = logging =>
                {
                    logging.AddXUnit(outputHelper);
                    logging.SetMinimumLevel(LogLevel.Debug);
                }
            };
            settings.AddFunction(new LambdaFunctionInfo(
                nameof(SimpleLambdaFunction),
                typeof(SimpleLambdaFunction),
                nameof(SimpleLambdaFunction.FunctionHandler)));
            var lambdaTestHost = await LambdaTestHost.Start(settings);

            var lambdaForwardUrlBuilder = new UriBuilder(lambdaTestHost.ServiceUrl);

            var isRunningInContainer = IsRunningInContainer();
            if (isRunningInContainer)
            {
                var localIpAddress = GetLocalIPAddress();
                lambdaForwardUrlBuilder.Host = localIpAddress;
            }
            else
            {
                lambdaForwardUrlBuilder.Host = "host.docker.internal";
            }

            var lambdaForwardUrl = lambdaForwardUrlBuilder.ToString();
            //  Remove trailing slash as localstack does string concatenation resulting in "//".
            lambdaForwardUrl = lambdaForwardUrl.Remove(lambdaForwardUrl.Length - 1);
            outputHelper.WriteLine($"Using LAMBDA_FALLBACK_URL={lambdaForwardUrl}");

            var localStackBuilder = new Builder()
                .UseContainer()
                .WithName($"lambda-testhost-localstack-{Guid.NewGuid()}")
                .UseImage("localstack/localstack:latest")
                .WithEnvironment(
                    "SERVICES=lambda",
                    "LS_LOG=debug",
                    $"LAMBDA_FORWARD_URL={lambdaForwardUrl}")
                .ExposePort(0, ContainerPort);
            var localStack = localStackBuilder.Build().Start();

            var exposedPort = localStack
                .GetConfiguration()
                .NetworkSettings
                .Ports.First()
                .Value.First()
                .HostPort;

            var localstackServiceUrl = new UriBuilder($"http://localhost:{exposedPort}");

            if (isRunningInContainer)
            {
                var host = localStack
                    .GetConfiguration()
                    .NetworkSettings
                    .IPAddress;

                localstackServiceUrl.Host = host;
                localstackServiceUrl.Port = ContainerPort;
            }

            outputHelper.WriteLine($"Using localstackServiceUrl={localstackServiceUrl}");
            return new LocalStackFixture(lambdaTestHost, localStack, localstackServiceUrl.Uri, outputHelper);
        }

        public async ValueTask DisposeAsync()
        {
            await LambdaTestHost.DisposeAsync();

            var hosts = new Hosts().Discover();
            var docker = hosts.FirstOrDefault(x => x.IsNative) ?? hosts.FirstOrDefault(x => x.Name == "default");

            await Task.Delay(1000);
            _outputHelper.WriteLine("--- Begin container logs ---");
            using (var logs = docker?.Host.Logs(_localStack.Id, certificates: docker.Certificates))
            {
                var line = logs!.Read();
                while (line != null)
                {
                    _outputHelper.WriteLine(line);
                    line = logs!.Read();
                }
            }
            _outputHelper.WriteLine("--- End container logs ---");

            _localStack.RemoveOnDispose = true;
            _localStack.Dispose();
        }

        public static bool IsRunningInContainer()
        {
            /*
            There are two scenarios where tests can being run:
            1. On docker host (i.e. development time).
            2. In a container.

            Depending on which will set determin the networking model and 
            host names.

            */

            var env = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER");
            return env != null && env.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetLocalIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            throw new Exception("No network adapters with an IPv4 address in the system!");
        }
    }
}