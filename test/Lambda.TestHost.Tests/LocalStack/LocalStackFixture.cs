﻿using System;
using System.Linq;
using System.Threading.Tasks;
using Ductus.FluentDocker.Builders;
using Ductus.FluentDocker.Commands;
using Ductus.FluentDocker.Services;
using Logicality.AWS.Lambda.TestHost.Functions;
using Xunit.Abstractions;

namespace Logicality.AWS.Lambda.TestHost.LocalStack
{
    public class LocalStackFixture : IAsyncDisposable
    {
        private readonly LambdaTestHost _lambdaTestHost;
        private readonly IContainerService _localStack;
        private readonly ITestOutputHelper _outputHelper;
        private const int ContainerPort = 4566;

        public LocalStackFixture(LambdaTestHost lambdaTestHost,
            IContainerService localStack,
            Uri serviceUrl,
            ITestOutputHelper outputHelper)
        {
            ServiceUrl = serviceUrl;
            _lambdaTestHost = lambdaTestHost;
            _localStack = localStack;
            _outputHelper = outputHelper;
        }
        
        public Uri ServiceUrl { get; }

        public static async Task<LocalStackFixture> Create(ITestOutputHelper outputHelper, string services)
        {
            // Runs a the Lambda TestHost (invoke api) on a random port
            var settings = new LambdaTestHostSettings(() => new TestLambdaContext());
            settings.AddFunction(new LambdaFunctionInfo(
                nameof(SimpleLambdaFunction),
                typeof(SimpleLambdaFunction),
                nameof(SimpleLambdaFunction.FunctionHandler)));
            var lambdaTestHost = await LambdaTestHost.Start(settings);

            var dockerInternal = new UriBuilder(lambdaTestHost.ServiceUrl);

            var localStack = new Builder()
                .UseContainer()
                .WithName($"lambda-testhost-localstack-{Guid.NewGuid()}")
                .UseImage("localstack/localstack:latest")
                .WithEnvironment(
                    "SERVICES={services}",
                    $"LAMBDA_FORWARD_URL={dockerInternal}")
                .ExposePort(0, ContainerPort)
                .WaitForPort($"{ContainerPort}/tcp", 10000, "127.0.0.1")
                .Build()
                .Start();

            var port = localStack
                .GetConfiguration()
                .NetworkSettings
                .Ports.First()
                .Value.First()
                .HostPort;

            var localstackServiceUrl = new Uri($"http://localhost:{port}");
            return new LocalStackFixture(lambdaTestHost, localStack, localstackServiceUrl, outputHelper);
        }

        public async ValueTask DisposeAsync()
        {
            await _lambdaTestHost.DisposeAsync();

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
    }
}