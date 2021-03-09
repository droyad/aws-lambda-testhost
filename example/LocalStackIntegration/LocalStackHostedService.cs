﻿using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;
using Ductus.FluentDocker.Builders;
using Ductus.FluentDocker.Services;
using Logicality.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LocalStackIntegration
{
    public class LocalStackHostedService : DockerHostedService
    {
        private readonly IConfiguration _configuration;
        private readonly HostedServiceContext _context;
        public const int Port = 4566;
        private const int ContainerPort = 4566;

        public LocalStackHostedService(
            IConfiguration configuration,
            HostedServiceContext context,
            ILogger<DockerHostedService> logger)
            : base(logger)
        {
            _configuration = configuration;
            _context = context;
        }

        protected override string ContainerName => "lambda-testhost-localstack";

        public Uri ServiceUrl { get; private set; }

        public AWSCredentials AWSCredentials { get; private set; } = new BasicAWSCredentials("not", "used");

        public string QueueUrl { get; private set; }

        protected override IContainerService CreateContainerService()
        {
            var dockerInternal = new UriBuilder(_context.LambdaTestHost.ServiceUrl)
            {
                Host = "host.docker.internal"
            };
            var localStackApiKey = _configuration.GetValue<string>("LocalStackApiKey");
            return new Builder()
                .UseContainer()
                .WithName(ContainerName)
                .UseImage("localstack/localstack:latest")
                .WithEnvironment(
                    "SERVICES=sqs",
                    $"LOCALSTACK_API_KEY={localStackApiKey}",
                    $"LAMBDA_FORWARD_URL={dockerInternal}")
                //.UseNetwork("host")
                .ReuseIfExists()
                .ExposePort(Port, ContainerPort)
                .ExposePort(443, 443)
                .WaitForPort($"{ContainerPort}/tcp", 10000, "127.0.0.1")
                .Build();
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            await base.StartAsync(cancellationToken);
            ServiceUrl = new Uri($"http://localhost:{Port}");
            _context.LocalStack = this;

            var sqsConfig = new AmazonSQSConfig
            {
                ServiceURL = ServiceUrl.ToString()
            };

            var sqsClient = new AmazonSQSClient(AWSCredentials, sqsConfig);
            var createQueueRequest = new CreateQueueRequest("test-queue");
            var createQueueResponse = await sqsClient.CreateQueueAsync(createQueueRequest, cancellationToken);
            var lambdaConfig = new AmazonLambdaConfig
            {
                ServiceURL = ServiceUrl.ToString()
            };

            var queueAttributesAsync = await sqsClient.GetQueueAttributesAsync(
                createQueueResponse.QueueUrl,
                new List<string>
                {
                    QueueAttributeName.All
                },
                cancellationToken);

            var lambdaClient = new AmazonLambdaClient(AWSCredentials, lambdaConfig);
            var createEventSourceMappingRequest = new CreateEventSourceMappingRequest
            {
                EventSourceArn = queueAttributesAsync.QueueARN,
                FunctionName = "simple"
            };
            try
            {
                var createEventSourceMappingResponse = await lambdaClient
                    .CreateEventSourceMappingAsync(createEventSourceMappingRequest, cancellationToken);
            }
            catch(Exception ex)
            {
                throw;
            }

            QueueUrl = createQueueResponse.QueueUrl;
        }
    }
}