using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AzureBusService.EndToEndTests;

public sealed class OrderFlowTests
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(60);

    public static bool EndToEndTestsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_END_TO_END_TESTS"), "true", StringComparison.OrdinalIgnoreCase);

    [Fact(
        Skip = "Set RUN_END_TO_END_TESTS=true to run Docker-backed end-to-end tests.",
        SkipUnless = nameof(EndToEndTestsEnabled),
        Timeout = 180_000)]
    public async Task AcceptedOrderIsProcessedToCompletion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = FindRepositoryRoot();
        var sqlConnectionString = GetRequiredEnvironmentVariable("E2E_SQL_CONNECTION_STRING");
        var serviceBusConnectionString = GetRequiredEnvironmentVariable("E2E_SERVICE_BUS_CONNECTION_STRING");
        var secrets = new[] { sqlConnectionString, serviceBusConnectionString };
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("Could not determine the build configuration.");
        var apiDll = GetBuiltDll(root, "AzureBusService.Api", configuration);
        var workerDll = GetBuiltDll(root, "AzureBusService.Worker", configuration);
        var apiPort = GetFreeTcpPort();
        var workerPort = GetFreeTcpPort(apiPort);
        var apiUrl = new Uri($"http://127.0.0.1:{apiPort}");
        var workerUrl = new Uri($"http://127.0.0.1:{workerPort}");

        ChildProcess? api = null;
        ChildProcess? worker = null;
        Exception? failure = null;

        try
        {
            api = StartApplication(apiDll, apiUrl, sqlConnectionString, serviceBusConnectionString, secrets);
            worker = StartApplication(workerDll, workerUrl, sqlConnectionString, serviceBusConnectionString, secrets);

            using var client = new HttpClient { BaseAddress = apiUrl, Timeout = TimeSpan.FromSeconds(5) };
            await WaitForHealthAsync(client, new Uri(apiUrl, "/health"), api, StartupTimeout, cancellationToken);
            await WaitForHealthAsync(client, new Uri(workerUrl, "/health"), worker, StartupTimeout, cancellationToken);

            var expectedTotal = 24.68m;
            var request = new
            {
                customerId = Guid.NewGuid(),
                currency = "USD",
                items = new[]
                {
                    new { productId = Guid.NewGuid(), quantity = 2, unitPrice = 12.34m },
                },
            };
            using var post = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
            {
                Content = JsonContent.Create(request),
            };
            post.Headers.Add("Idempotency-Key", $"e2e-{Guid.NewGuid():N}");
            using var acceptedResponse = await client.SendAsync(post, cancellationToken);
            Assert.Equal(HttpStatusCode.Accepted, acceptedResponse.StatusCode);

            using var accepted = JsonDocument.Parse(
                await acceptedResponse.Content.ReadAsStreamAsync(cancellationToken));
            var orderId = accepted.RootElement.GetProperty("orderId").GetGuid();
            var statusUrl = accepted.RootElement.GetProperty("statusUrl").GetString();
            Assert.NotEqual(Guid.Empty, orderId);
            Assert.Equal("Pending", accepted.RootElement.GetProperty("status").GetString());
            Assert.Equal($"/api/v1/orders/{orderId:D}", statusUrl);

            using var completed = await PollUntilCompletedAsync(
                client,
                statusUrl!,
                CompletionTimeout,
                cancellationToken);
            Assert.Equal(orderId, completed.RootElement.GetProperty("orderId").GetGuid());
            Assert.Equal("Completed", completed.RootElement.GetProperty("status").GetString());
            Assert.Equal(expectedTotal, completed.RootElement.GetProperty("total").GetDecimal());
            Assert.Equal(JsonValueKind.Null, completed.RootElement.GetProperty("failure").ValueKind);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                if (worker is not null)
                {
                    await worker.DisposeAsync();
                }
            }
            finally
            {
                if (api is not null)
                {
                    await api.DisposeAsync();
                }
            }
        }

        if (failure is not null)
        {
            Assert.Fail($"{failure}\n\nAPI output:\n{api?.Diagnostics}\n\nWorker output:\n{worker?.Diagnostics}");
        }
    }

    private static ChildProcess StartApplication(
        string dllPath,
        Uri url,
        string sqlConnectionString,
        string serviceBusConnectionString,
        string[] secrets)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(dllPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(dllPath);
        startInfo.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        startInfo.Environment["DOTNET_ENVIRONMENT"] = "Development";
        startInfo.Environment["ASPNETCORE_URLS"] = url.AbsoluteUri;
        startInfo.Environment["ConnectionStrings__Orders"] = sqlConnectionString;
        startInfo.Environment["Authentication__Enabled"] = "false";
        startInfo.Environment["ServiceBus__Mode"] = "Emulator";
        startInfo.Environment["ServiceBus__ConnectionString"] = serviceBusConnectionString;
        startInfo.Environment["ServiceBus__QueueName"] = "orders";
        return ChildProcess.Start(startInfo, secrets);
    }

    private static async Task WaitForHealthAsync(
        HttpClient client,
        Uri endpoint,
        ChildProcess process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (process.HasExited)
            {
                throw new InvalidOperationException($"{Path.GetFileName(process.FileName)} exited before becoming healthy.");
            }

            try
            {
                using var response = await client.GetAsync(endpoint, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The listener may not be bound yet.
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Retry an individual request until the overall startup timeout expires.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for {endpoint}.");
    }

    private static async Task<JsonDocument> PollUntilCompletedAsync(
        HttpClient client,
        string statusUrl,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        string? lastResponse = null;
        while (stopwatch.Elapsed < timeout)
        {
            using var response = await client.GetAsync(statusUrl, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            lastResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = JsonDocument.Parse(lastResponse);
            var status = document.RootElement.GetProperty("status").GetString();
            if (status is "Completed" or "Failed")
            {
                return document;
            }

            document.Dispose();
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        throw new TimeoutException($"Order did not complete within {timeout}. Last response: {lastResponse}");
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AzureBusService.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate AzureBusService.sln from the test assembly directory.");
    }

    private static string GetBuiltDll(string root, string projectName, string configuration)
    {
        var path = Path.Combine(root, "src", projectName, "bin", configuration, "net10.0", $"{projectName}.dll");
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"Built application DLL was not found: {path}", path);
    }

    private static string GetRequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable {name} is required.");

    private static int GetFreeTcpPort(int excludedPort = -1)
    {
        int port;
        do
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        while (port == excludedPort);

        return port;
    }

    private sealed class ChildProcess(Process process, Task<string> standardOutput, Task<string> standardError, string[] secrets)
        : IAsyncDisposable
    {
        public string Diagnostics { get; private set; } = "No output captured.";

        public string FileName => process.StartInfo.ArgumentList[0];

        public bool HasExited => process.HasExited;

        public static ChildProcess Start(ProcessStartInfo startInfo, string[] secrets)
        {
            var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException($"Failed to start {startInfo.ArgumentList[0]}.");
            }

            return new ChildProcess(
                process,
                process.StandardOutput.ReadToEndAsync(),
                process.StandardError.ReadToEndAsync(),
                secrets);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync();
                var output = await standardOutput;
                var error = await standardError;
                Diagnostics = Redact($"stdout:\n{output}\nstderr:\n{error}");
            }
            finally
            {
                process.Dispose();
            }
        }

        private string Redact(string value)
        {
            var redacted = new StringBuilder(value);
            foreach (var secret in secrets)
            {
                redacted.Replace(secret, "[REDACTED]");
            }

            return redacted.ToString();
        }
    }
}
