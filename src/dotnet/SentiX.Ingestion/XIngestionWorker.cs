using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using SentiX.Domain;

namespace SentiX.Ingestion;

public sealed class XIngestionWorker(
    XOptions options,
    UniverseConfig universe,
    IMarketEventPipeline pipeline,
    HttpClient http,
    TimeProvider clock,
    ILogger<XIngestionWorker> logger) : BackgroundService
{
    private const string RulesUrl = "https://api.x.com/2/tweets/search/stream/rules";
    private const string StreamUrl = "https://api.x.com/2/tweets/search/stream" +
        "?tweet.fields=author_id,created_at,referenced_tweets&expansions=author_id&user.fields=username";
    private const string RuleTag = "sentix-allowlist";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.HasCredentials)
        {
            logger.LogInformation("X ingestion skipped because the bearer token is missing.");
            return;
        }

        if (!pipeline.IsConfigured || options.DailyPostCap <= 0)
        {
            logger.LogInformation("X ingestion skipped because PostgreSQL or a positive daily post cap is unconfigured.");
            return;
        }

        string? rule;
        IReadOnlySet<string> allowedHandles;
        try
        {
            rule = XRuleBuilder.Build(universe);
            allowedHandles = XRuleBuilder.GetHandles(universe).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning("{Reason} X ingestion skipped.", ex.Message);
            return;
        }

        if (rule is null)
        {
            logger.LogInformation("X ingestion skipped because the allowlist contains no X handles.");
            return;
        }

        var budget = new XDailyPostBudget(options.DailyPostCap);
        int retrySeconds = 1;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await pipeline.EnsureReadyAsync(stoppingToken);
                await RefreshBudgetAsync(budget, stoppingToken);
                if (budget.IsExhausted)
                {
                    logger.LogWarning("X daily post cap of {Cap} reached. Stream paused until {Reset:O}.",
                        budget.DailyLimit, budget.ResetsAtUtc);
                    var remaining = budget.ResetsAtUtc - clock.GetUtcNow();
                    if (remaining > TimeSpan.Zero)
                    {
                        await Task.Delay(remaining, clock, stoppingToken);
                    }

                    continue;
                }

                await SynchronizeRuleAsync(rule, stoppingToken);
                await ReadStreamAsync(allowedHandles, budget, () => retrySeconds = 1, stoppingToken);
                if (budget.IsExhausted)
                {
                    continue;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or
                NpgsqlException or DbUpdateException or TimeoutException or OperationCanceledException)
            {
                logger.LogWarning("X stream interrupted ({ErrorType}). Reconnecting in {Seconds}s.",
                    ex.GetType().Name, retrySeconds);
            }

            await Task.Delay(TimeSpan.FromSeconds(retrySeconds), clock, stoppingToken);
            retrySeconds = Math.Min(retrySeconds * 2, 60);
        }
    }

    private async Task RefreshBudgetAsync(XDailyPostBudget budget, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        if (start != budget.StartOfDayUtc)
        {
            budget.StartDay(start, await pipeline.CountXEventsAsync(start, token));
        }
    }

    private async Task SynchronizeRuleAsync(string rule, CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, RulesUrl);
        var rules = await ReadRulesAsync(request, token);
        if (rules.Any(existing => existing.Tag != RuleTag))
        {
            throw new InvalidDataException("X stream contains rules outside the SentiX allowlist.");
        }

        if (rules.Length == 1 && rules[0].Value == rule)
        {
            return;
        }

        if (rules.Length > 0)
        {
            using var delete = Request(HttpMethod.Post, RulesUrl);
            delete.Content = JsonContent.Create(new { delete = new { ids = rules.Select(existing => existing.Id).ToArray() } });
            await ReadRulesAsync(delete, token);
        }

        using var add = Request(HttpMethod.Post, RulesUrl);
        add.Content = JsonContent.Create(new { add = new[] { new { value = rule, tag = RuleTag } } });
        await ReadRulesAsync(add, token);

        using var verify = Request(HttpMethod.Get, RulesUrl);
        var refreshed = await ReadRulesAsync(verify, token);
        if (refreshed.Length != 1 || refreshed[0].Tag != RuleTag || refreshed[0].Value != rule)
        {
            throw new InvalidDataException("X stream allowlist could not be verified.");
        }
    }

    private async Task<StreamRule[]> ReadRulesAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await http.SendAsync(request, deadline.Token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<RuleResponse>(cancellationToken: deadline.Token);
        if (result is null || result.Errors is { Length: > 0 } || result.Meta?.NextToken is not null)
        {
            throw new InvalidDataException("X stream rules response is incomplete or reports an error.");
        }

        return result.Data ?? [];
    }

    private async Task ReadStreamAsync(
        IReadOnlySet<string> allowedHandles, XDailyPostBudget budget, Action receivedPost, CancellationToken token)
    {
        using var request = Request(HttpMethod.Get, StreamUrl);
        using var response = await SendStreamRequestAsync(request, token);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(token));
        while (true)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var line = await reader.ReadLineAsync(deadline.Token);
            if (line is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var body = JsonDocument.Parse(line);
            if (body.RootElement.ValueKind != JsonValueKind.Object ||
                !body.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            await RefreshBudgetAsync(budget, token);
            if (budget.IsExhausted)
            {
                return;
            }

            budget.CountPost();
            if (budget.IsExhausted)
            {
                // Disconnect immediately at the cap before the last row's database/native round-trip.
                reader.Dispose();
                response.Dispose();
            }

            var marketEvent = ParsePost(body.RootElement, data, allowedHandles);
            if (marketEvent is not null)
            {
                await pipeline.ProcessAsync(marketEvent, line, token);
                receivedPost();
            }

            if (budget.IsExhausted)
            {
                return;
            }
        }
    }

    private async Task<HttpResponseMessage> SendStreamRequestAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
    }

    private static MarketEvent? ParsePost(JsonElement body, JsonElement data, IReadOnlySet<string> allowlist)
    {
        if (ReadString(data, "author_id") is not { Length: > 0 } authorId ||
            ReadString(data, "id") is not { Length: > 0 } id ||
            ReadString(data, "text") is not { Length: > 0 } headline ||
            !data.TryGetProperty("created_at", out var created) || created.ValueKind != JsonValueKind.String ||
            !created.TryGetDateTimeOffset(out var published) ||
            !body.TryGetProperty("includes", out var includes) || includes.ValueKind != JsonValueKind.Object ||
            !includes.TryGetProperty("users", out var users) || users.ValueKind != JsonValueKind.Array ||
            data.TryGetProperty("referenced_tweets", out var referenced) && (referenced.ValueKind != JsonValueKind.Array ||
                referenced.EnumerateArray().Any(reference => ReadString(reference, "type") is not "quoted")))
        {
            return null;
        }

        foreach (var user in users.EnumerateArray())
        {
            var handle = ReadString(user, "username");
            if (ReadString(user, "id") == authorId && handle is not null && allowlist.Contains(handle))
            {
                return new MarketEvent($"x:{id}", EventSource.XAllowlist, $"@{handle}", published, headline, "", "", []);
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken!.Trim());
        return request;
    }

    private sealed record StreamRule(string Id, string Value, string Tag);
    private sealed record RuleMeta([property: System.Text.Json.Serialization.JsonPropertyName("next_token")] string? NextToken);
    private sealed record RuleResponse(StreamRule[]? Data, JsonElement[]? Errors, RuleMeta? Meta);
}
