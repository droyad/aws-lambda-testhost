using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Logicality.AWS.Lambda.TestHost.LocalStack
{
    public class LocalStackIntegrationsTests
    {
        private readonly ITestOutputHelper _outputHelper;

        public LocalStackIntegrationsTests(ITestOutputHelper outputHelper)
        {
            _outputHelper = outputHelper;
        }

        [Fact]
        public async Task With_lambda_service_and_LAMBDA_FORWARD_URL_then_should_invoke() 
        {
            await using var fixture = await LocalStackFixture.Create(_outputHelper);

            // 1. Arrange: Create the Lambda Client
            var lambdaClient = new AmazonLambdaClient(fixture.AWSCredentials, new AmazonLambdaConfig
            {
                ServiceURL = fixture.ServiceUrl.ToString()
            });

            var functionInfo = fixture.LambdaTestHost.Settings.Functions.First().Value;
            var createFunctionRequest = new CreateFunctionRequest
            {
                Handler = "dummy-handler", // ignored
                FunctionName = functionInfo.Name,
                Role = "arn:aws:iam::123456789012:role/foo", // must be specified
                Code = new FunctionCode
                {
                    ZipFile = new MemoryStream() // must be specified but is ignored
                }
            };
            await lambdaClient.CreateFunctionAsync(createFunctionRequest);

            // 2. Act: Call lambda Invoke API
            var invokeRequest = new InvokeRequest
            {
                FunctionName = functionInfo.Name,
                Payload = "{\"Data\":\"Bar\"}",
            };
            var invokeResponse = await lambdaClient.InvokeAsync(invokeRequest);

            // 3. Assert: Check payload
            invokeResponse.HttpStatusCode.ShouldBe(HttpStatusCode.OK);
            invokeResponse.FunctionError.ShouldBeNullOrEmpty();
            var responsePayload = Encoding.UTF8.GetString(invokeResponse.Payload.ToArray());
            responsePayload.ShouldStartWith("{\"Reverse\":\"raB\"}");
        }
    }
}
 