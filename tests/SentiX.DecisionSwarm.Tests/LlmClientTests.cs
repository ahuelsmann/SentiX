using System.Net;
using System.Text.Json;

namespace SentiX.DecisionSwarm.Tests;

public sealed class LlmClientTests
{
    private static LlmCompletionRequest FastRequest() =>
        new(LlmTier.Fast, AgentRole.FactAnalyst, "SYSTEM-PROMPT", "USER-PROMPT", 0.1, 512);

    private static LlmCompletionRequest FastSchemaRequest() =>
        new(LlmTier.Fast, AgentRole.FactAnalyst, "SYSTEM-PROMPT", "USER-PROMPT", 0.1, 512,
            AgentJsonSchemas.FactAnalyst, AgentJsonSchemas.NameFor(AgentRole.FactAnalyst));

    private static LlmCompletionRequest JudgeRequest() =>
        new(LlmTier.Judge, AgentRole.JudgeAgent, "JUDGE-SYSTEM", "JUDGE-USER", 0.1, 768);

    private static LlmCompletionRequest JudgeSchemaRequest() =>
        new(LlmTier.Judge, AgentRole.JudgeAgent, "JUDGE-SYSTEM", "JUDGE-USER", 0.1, 768,
            AgentJsonSchemas.JudgeAgent, AgentJsonSchemas.NameFor(AgentRole.JudgeAgent));

    private const string OpenAiFencedResponse =
        """{"id":"chatcmpl-1","model":"gpt-4o-mini-2024-07-18","choices":[{"index":0,"message":{"role":"assistant","content":"```json\n{\"isActionable\":true,\"noveltyScore\":0.9}\n```"}}],"usage":{"prompt_tokens":120,"completion_tokens":30,"total_tokens":150}}""";

    private const string AnthropicResponse =
        """{"id":"msg_1","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"text","text":"```json\n{\"action\":\"HOLD\"}\n```"}],"usage":{"input_tokens":200,"output_tokens":50}}""";

    [Fact]
    public async Task OpenAiCompatible_PostsChatCompletionsPayload_AndUnwrapsFencedJson()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, OpenAiFencedResponse));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        var result = await client.CompleteJsonAsync(FastRequest(), CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal(new Uri("https://api.openai.com/v1/chat/completions"), handler.RequestUri);
        Assert.Equal($"Bearer {Fixtures.FastKey}", handler.Headers["Authorization"]);

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.Equal("gpt-4o-mini", root.GetProperty("model").GetString());
        Assert.Equal(0.1, root.GetProperty("temperature").GetDouble(), 6);
        Assert.Equal(512, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        var messages = root.GetProperty("messages");
        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("SYSTEM-PROMPT", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("USER-PROMPT", messages[1].GetProperty("content").GetString());

        Assert.Equal("""{"isActionable":true,"noveltyScore":0.9}""", result.Json);
        Assert.Equal(120, result.PromptTokens);
        Assert.Equal(30, result.CompletionTokens);
        Assert.Equal(150, result.TotalTokens);
        Assert.Equal("gpt-4o-mini-2024-07-18", result.Model);
    }

    [Fact]
    public async Task OpenAiCompatible_UsesLoopbackBaseUrlOverride()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, OpenAiFencedResponse));
        var options = Fixtures.ConfiguredOptions();
        options.FastBaseUrl = "http://localhost:11434/v1";
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler), options);

        await client.CompleteJsonAsync(FastRequest(), CancellationToken.None);

        Assert.Equal(new Uri("http://localhost:11434/v1/chat/completions"), handler.RequestUri);
    }

    [Fact]
    public async Task OpenAiCompatible_SumsTokensWhenTotalMissing()
    {
        const string response =
            """{"choices":[{"message":{"content":"{\"a\":1}"}}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, response));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        var result = await client.CompleteJsonAsync(FastRequest(), CancellationToken.None);

        Assert.Equal(15, result.TotalTokens);
        Assert.Equal("gpt-4o-mini", result.Model);
    }

    [Fact]
    public async Task Anthropic_PostsMessagesPayload_WithApiKeyHeader_AndParsesTextBlocks()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, AnthropicResponse));
        var client = new AnthropicLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        var result = await client.CompleteJsonAsync(JudgeRequest(), CancellationToken.None);

        Assert.Equal(new Uri("https://api.anthropic.com/v1/messages"), handler.RequestUri);
        Assert.Equal(Fixtures.JudgeKey, handler.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", handler.Headers["anthropic-version"]);
        Assert.False(handler.Headers.ContainsKey("anthropic-beta"));
        Assert.False(handler.Headers.ContainsKey("Authorization"));

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.Equal("claude-sonnet-4-5", root.GetProperty("model").GetString());
        Assert.Equal("JUDGE-SYSTEM", root.GetProperty("system").GetString());
        Assert.Equal(768, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.1, root.GetProperty("temperature").GetDouble(), 6);
        var messages = root.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("JUDGE-USER", messages[0].GetProperty("content").GetString());

        Assert.Equal("""{"action":"HOLD"}""", result.Json);
        Assert.Equal(200, result.PromptTokens);
        Assert.Equal(50, result.CompletionTokens);
        Assert.Equal(250, result.TotalTokens);
    }

    [Fact]
    public async Task OpenAiCompatible_WithJsonSchema_SendsStrictJsonSchemaResponseFormat()
    {
        var payload = JsonSerializer.Serialize(new
        {
            id = "chatcmpl-2",
            model = "gpt-4o-mini",
            choices = new[] { new { message = new { content = Fixtures.FactJson } } },
            usage = new { prompt_tokens = 10, completion_tokens = 20, total_tokens = 30 }
        });
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, payload));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        var result = await client.CompleteJsonAsync(FastSchemaRequest(), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.Body!);
        var responseFormat = body.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", responseFormat.GetProperty("type").GetString());
        var jsonSchema = responseFormat.GetProperty("json_schema");
        Assert.Equal("sentix_fact_analyst", jsonSchema.GetProperty("name").GetString());
        Assert.True(jsonSchema.GetProperty("strict").GetBoolean());

        var schema = jsonSchema.GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.True(schema.GetProperty("properties").TryGetProperty("isActionable", out _));
        Assert.Equal(Fixtures.FactJson, result.Json);
    }

    [Fact]
    public async Task Anthropic_WithJsonSchema_SendsForcedTool_AndParsesToolUseInput()
    {
        var toolUseResponse =
            $$$"""{"id":"msg_2","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[{"type":"tool_use","id":"toolu_1","name":"sentix_judge_agent","input":{{{Fixtures.JudgeJson("HOLD")}}}}],"usage":{"input_tokens":150,"output_tokens":45}}""";
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, toolUseResponse));
        var client = new AnthropicLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        var result = await client.CompleteJsonAsync(JudgeSchemaRequest(), CancellationToken.None);

        Assert.Equal(AnthropicLlmClient.StructuredOutputsBeta, handler.Headers["anthropic-beta"]);

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        var tools = root.GetProperty("tools");
        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("sentix_judge_agent", tools[0].GetProperty("name").GetString());
        Assert.True(tools[0].GetProperty("strict").GetBoolean());
        var inputSchema = tools[0].GetProperty("input_schema");
        Assert.Equal("object", inputSchema.GetProperty("type").GetString());
        Assert.False(inputSchema.GetProperty("additionalProperties").GetBoolean());
        Assert.True(inputSchema.GetProperty("properties").TryGetProperty("proposalId", out _));

        var toolChoice = root.GetProperty("tool_choice");
        Assert.Equal("tool", toolChoice.GetProperty("type").GetString());
        Assert.Equal("sentix_judge_agent", toolChoice.GetProperty("name").GetString());

        Assert.Equal(Fixtures.JudgeJson("HOLD"), result.Json);
        Assert.Equal(150, result.PromptTokens);
        Assert.Equal(45, result.CompletionTokens);
        Assert.Equal(195, result.TotalTokens);
    }

    [Fact]
    public async Task Anthropic_WithJsonSchema_RejectsTextOnlyResponse_WithoutToolUse()
    {
        var textOnlyResponse = JsonSerializer.Serialize(new
        {
            id = "msg_3",
            model = "claude-sonnet-4-5",
            content = new[] { new { type = "text", text = Fixtures.JudgeJson("HOLD") } },
            usage = new { input_tokens = 10, output_tokens = 10 }
        });
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, textOnlyResponse));
        var client = new AnthropicLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        var ex = await Assert.ThrowsAsync<LlmResponseFormatException>(() =>
            client.CompleteJsonAsync(JudgeSchemaRequest(), CancellationToken.None));

        Assert.True(ex.IsTransient);
    }

    [Theory]
    [InlineData("""{"content":[{"type":"tool_use","id":"toolu_1","name":"wrong_tool","input":{"action":"HOLD"}}]}""")]
    [InlineData("""{"content":[{"type":"tool_use","id":"toolu_1","name":"sentix_judge_agent","input":"not-an-object"}]}""")]
    public async Task Anthropic_WithJsonSchema_RejectsWrongToolOrNonObjectInput(string response)
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, response));
        var client = new AnthropicLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        var ex = await Assert.ThrowsAsync<LlmResponseFormatException>(() =>
            client.CompleteJsonAsync(JudgeSchemaRequest(), CancellationToken.None));

        Assert.True(ex.IsTransient);
    }

    [Theory]
    [InlineData("Here is the JSON:\n{\"action\":\"HOLD\"}")]
    [InlineData("{\"action\":\"HOLD\"}\nHope this helps!")]
    [InlineData("Sure! {\"action\":\"HOLD\"} Done.")]
    [InlineData("Here is the JSON:\n```json\n{\"action\":\"HOLD\"}\n```")]
    [InlineData("```json\n{\"action\":\"HOLD\"}\n```\nDone.")]
    public async Task BothClients_RejectProseWrappedJson(string modelText)
    {
        var openAiPayload = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = modelText } } }
        });
        var anthropicPayload = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = modelText } }
        });

        var openAi = new OpenAiCompatibleLlmClient(
            new HttpClient(new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, openAiPayload))),
            Fixtures.ConfiguredOptions());
        var anthropic = new AnthropicLlmClient(
            new HttpClient(new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, anthropicPayload))),
            Fixtures.ConfiguredOptions());

        var openAiEx = await Assert.ThrowsAsync<LlmResponseFormatException>(() =>
            openAi.CompleteJsonAsync(FastSchemaRequest(), CancellationToken.None));
        var anthropicFreeEx = await Assert.ThrowsAsync<LlmResponseFormatException>(() =>
            anthropic.CompleteJsonAsync(JudgeRequest(), CancellationToken.None));
        var anthropicSchemaEx = await Assert.ThrowsAsync<LlmResponseFormatException>(() =>
            anthropic.CompleteJsonAsync(JudgeSchemaRequest(), CancellationToken.None));

        Assert.True(openAiEx.IsTransient);
        Assert.True(anthropicFreeEx.IsTransient);
        Assert.True(anthropicSchemaEx.IsTransient);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.RequestTimeout, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task HttpErrors_MapToTransientOrPermanent_WithoutLeakingSecrets(HttpStatusCode status, bool transient)
    {
        var handler = new StubHttpMessageHandler(_ =>
            StubHttpMessageHandler.Json(status, "{\"error\":{\"message\":\"bad key " + Fixtures.JudgeKey + "\"}}"));
        var openAi = new OpenAiCompatibleLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());
        var anthropic = new AnthropicLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        var first = await Assert.ThrowsAsync<LlmException>(() => openAi.CompleteJsonAsync(FastRequest(), CancellationToken.None));
        var second = await Assert.ThrowsAsync<LlmException>(() => anthropic.CompleteJsonAsync(JudgeRequest(), CancellationToken.None));

        foreach (var ex in new[] { first, second })
        {
            Assert.Equal(transient, ex.IsTransient);
            Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), ex.Message);
            Assert.DoesNotContain(Fixtures.FastKey, ex.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Fixtures.JudgeKey, ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NetworkError_IsTransient()
    {
        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        var ex = await Assert.ThrowsAsync<LlmException>(() => client.CompleteJsonAsync(FastRequest(), CancellationToken.None));

        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task OversizedResponse_IsRejectedByCap()
    {
        var huge = new string('x', LlmHttp.MaxResponseBytes + 10);
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            "{\"choices\":[{\"message\":{\"content\":\"" + huge + "\"}}]}"));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        var ex = await Assert.ThrowsAsync<LlmResponseFormatException>(() =>
            client.CompleteJsonAsync(FastRequest(), CancellationToken.None));

        Assert.True(ex.IsTransient);
    }

    [Theory]
    [InlineData("""{"choices":[{"message":{"content":"I cannot comply."}}]}""")]
    [InlineData("""{"choices":[{"message":{"content":"```json\n{not json}\n```"}}]}""")]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"message":{"content":null}}]}""")]
    [InlineData("""not-json-at-all""")]
    public async Task MalformedModelOutput_ThrowsRetryableFormatException(string response)
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, response));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        var ex = await Assert.ThrowsAsync<LlmResponseFormatException>(() =>
            client.CompleteJsonAsync(FastRequest(), CancellationToken.None));

        Assert.True(ex.IsTransient);
    }

    [Fact]
    public async Task Anthropic_WithoutTextBlocks_ThrowsFormatException()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK,
            """{"content":[{"type":"tool_use","id":"x"}],"usage":{"input_tokens":1,"output_tokens":1}}"""));
        var client = new AnthropicLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        await Assert.ThrowsAsync<LlmResponseFormatException>(() => client.CompleteJsonAsync(JudgeRequest(), CancellationToken.None));
    }

    [Fact]
    public async Task UnconfiguredOrMismatchedTier_NeverSendsHttp()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, OpenAiFencedResponse));
        var unconfigured = new OpenAiCompatibleLlmClient(new HttpClient(handler),
            new LlmOptions { FastApiKey = "your_key", JudgeApiKey = "your_key" });
        var mismatched = new OpenAiCompatibleLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());
        var anthropicMismatch = new AnthropicLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());

        var a = await Assert.ThrowsAsync<LlmException>(() => unconfigured.CompleteJsonAsync(FastRequest(), CancellationToken.None));
        var b = await Assert.ThrowsAsync<LlmException>(() => mismatched.CompleteJsonAsync(JudgeRequest(), CancellationToken.None));
        var c = await Assert.ThrowsAsync<LlmException>(() => anthropicMismatch.CompleteJsonAsync(FastRequest(), CancellationToken.None));

        Assert.False(a.IsTransient);
        Assert.False(b.IsTransient);
        Assert.False(c.IsTransient);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAsOperationCanceled()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(HttpStatusCode.OK, OpenAiFencedResponse));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CompleteJsonAsync(FastRequest(), cts.Token));
    }

    [Fact]
    public async Task TieredClient_RoutesByConfiguredTierProvider()
    {
        var openAi = new ScriptedLlmClient();
        var anthropic = new ScriptedLlmClient();
        var router = new TieredLlmClient(Fixtures.ConfiguredOptions(), openAi, anthropic);

        await router.CompleteJsonAsync(FastRequest(), CancellationToken.None);
        await router.CompleteJsonAsync(JudgeRequest(), CancellationToken.None);

        Assert.Equal(1, openAi.TotalCalls);
        Assert.Equal(AgentRole.FactAnalyst, openAi.Requests.Single().Role);
        Assert.Equal(1, anthropic.TotalCalls);
        Assert.Equal(AgentRole.JudgeAgent, anthropic.Requests.Single().Role);
    }

    [Fact]
    public async Task TieredClient_UnconfiguredTier_ThrowsPermanent()
    {
        var router = new TieredLlmClient(new LlmOptions(), new ScriptedLlmClient(), new ScriptedLlmClient());

        var ex = await Assert.ThrowsAsync<LlmException>(() => router.CompleteJsonAsync(FastRequest(), CancellationToken.None));

        Assert.False(ex.IsTransient);
    }

    [Fact]
    public async Task MidStreamBodyIOException_ClassifiesAsTransient_AndRetriesToSuccess()
    {
        var attempts = 0;
        var okPayload = JsonSerializer.Serialize(new
        {
            id = "ok",
            model = "gpt-4o-mini",
            choices = new[] { new { message = new { content = Fixtures.NonActionableFactJson } } },
            usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
        });
        var handler = new StubHttpMessageHandler(_ =>
        {
            attempts++;
            return attempts == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new MidStreamFailingContent("{\"choices\":[{\"message\":{\"") }
                : StubHttpMessageHandler.Json(HttpStatusCode.OK, okPayload);
        });

        var openAi = new OpenAiCompatibleLlmClient(new HttpClient(handler), Fixtures.ConfiguredOptions());
        var first = await Assert.ThrowsAsync<LlmException>(() =>
            openAi.CompleteJsonAsync(FastSchemaRequest(), CancellationToken.None));
        Assert.True(first.IsTransient);

        // Reset and run through orchestrator to verify 200 OK + mid-stream IOException on attempt 1 triggers retry and succeeds on attempt 2.
        attempts = 0;
        var tiered = new TieredLlmClient(Fixtures.ConfiguredOptions(), openAi, new ScriptedLlmClient());
        var orchestrator = new DecisionSwarmOrchestrator(tiered, Fixtures.ConfiguredOptions());

        var outcome = await orchestrator.EvaluateAsync(Fixtures.Request(), CancellationToken.None);

        Assert.Equal(2, attempts);
        Assert.False(outcome.FallbackUsed);
        Assert.Null(outcome.FailureReason);
        Assert.Equal(SentiX.Domain.TradeAction.Hold, outcome.Proposal.Action);
        Assert.Equal(Fixtures.NonActionableFactJson, outcome.FactJson);
    }

    [Theory]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("  ```json\n{\"a\":1}\n```  ", "{\"a\":1}")]
    [InlineData("```\n{\"a\":{\"b\":2}}\n```", "{\"a\":{\"b\":2}}")]
    public void ExtractJsonObject_UnwrapsCleanFencesOnly(string input, string expected)
    {
        Assert.Equal(expected, LlmHttp.ExtractJsonObject(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("```json")]
    [InlineData("```python\n{\"a\":1}\n```")]
    [InlineData("{\"a\":}")]
    [InlineData("Sure! {\"a\":1} Hope this helps.")]
    [InlineData("Here:\n```json\n{\"a\":1}\n```")]
    [InlineData("```json\n{\"a\":1}\n```\nDone.")]
    [InlineData("{\"a\":1} {\"b\":2}")]
    public void ExtractJsonObject_RejectsNonObjectsAndProse(string? input)
    {
        Assert.Throws<LlmResponseFormatException>(() => LlmHttp.ExtractJsonObject(input));
    }

    private sealed class MidStreamFailingContent(string partialPrefix) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new NotSupportedException();

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MidStreamFailingStream(System.Text.Encoding.UTF8.GetBytes(partialPrefix)));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class MidStreamFailingStream(byte[] prefix) : Stream
    {
        private bool _sentPrefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset mid-body");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sentPrefix && prefix.Length > 0 && buffer.Length >= prefix.Length)
            {
                _sentPrefix = true;
                prefix.CopyTo(buffer);
                return ValueTask.FromResult(prefix.Length);
            }

            return ValueTask.FromException<int>(new IOException("connection reset mid-body"));
        }
    }
}
