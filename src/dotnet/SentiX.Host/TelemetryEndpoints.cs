using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SentiX.DecisionSwarm;
using SentiX.Domain;
using SentiX.Infrastructure;
using SentiX.Ingestion;
using SentiX.NativeBridge;

namespace SentiX.Host;

internal static class TelemetryEndpoints
{
    internal static IEndpointRouteBuilder MapTelemetryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", async (
            NativeBridgeClient bridge,
            LlmOptions llmOptions,
            IConfiguration config,
            TimeProvider clock,
            IDbContextFactory<SentiXDbContext> dbFactory,
            AlpacaOptions alpaca,
            FinnhubOptions finnhub,
            SecOptions sec,
            XOptions xOptions,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var nativeStatus = await ReadNativeStatusAsync(bridge, context.RequestAborted);
            var dbConfigured = HasDatabaseConnection(config);
            var dbReachable = dbConfigured && await CheckDatabaseReachableAsync(dbFactory, context.RequestAborted);
            var ingestionEnabled = alpaca.HasCredentials || !string.IsNullOrWhiteSpace(finnhub.ApiKey) ||
                                   (!string.IsNullOrWhiteSpace(sec.UserAgent) && !sec.UserAgent.Trim().Equals("disabled", StringComparison.OrdinalIgnoreCase)) ||
                                   !string.IsNullOrWhiteSpace(xOptions.BearerToken);

            return Results.Ok(new
            {
                status = "ok",
                native = nativeStatus,
                nativeScorerReady = nativeStatus == "ready",
                phase = 3,
                tradingEnabled = false,
                orderExecutionEnabled = false,
                decisionSwarmEnabled = true,
                decisionSwarmConfigured = llmOptions.IsConfigured,
                databaseConfigured = dbConfigured,
                databaseReachable = dbReachable,
                ingestionEnabled,
                utcNow = clock.GetUtcNow()
            });
        });

        endpoints.MapGet("/api/overview", async (
            NativeBridgeClient bridge,
            LlmOptions llmOptions,
            IMarketQuoteCache quoteCache,
            IConfiguration config,
            IDbContextFactory<SentiXDbContext> dbFactory,
            TimeProvider clock,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var ct = context.RequestAborted;
            var now = clock.GetUtcNow();
            var nativeStatus = await ReadNativeStatusAsync(bridge, ct);
            var quotes = quoteCache.GetAll().Select(ToQuoteDto).ToArray();
            var dbConfigured = HasDatabaseConnection(config);

            if (!dbConfigured)
            {
                return Results.Ok(new
                {
                    generatedAtUtc = now,
                    phase = 3,
                    nativeStatus,
                    nativeScorerReady = nativeStatus == "ready",
                    tradingEnabled = false,
                    orderExecutionEnabled = false,
                    databaseConfigured = false,
                    decisionSwarmConfigured = llmOptions.IsConfigured,
                    counts = new
                    {
                        events = 0,
                        fastPathPassed = 0,
                        debates = 0,
                        riskEvaluated = 0,
                        riskApproved = 0,
                        shadowApproved = 0,
                        shadowBlocked = 0,
                        cachedQuotes = quotes.Length,
                        quotes = quotes.Length,
                        upcomingCalendar = 0,
                        scheduledEvents = 0
                    },
                    latestDebates = Array.Empty<object>(),
                    quotes
                });
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var eventsCount = await db.MarketEvents.CountAsync(ct);
            var fastPathPassedCount = await db.FastPathResults.CountAsync(r => r.PassToDecisionSwarm, ct);
            var debatesCount = await db.DecisionDebates.CountAsync(ct);
            var riskEvaluatedCount = await db.RiskEvaluations.CountAsync(ct);
            var shadowApprovedCount = await db.RiskEvaluations.CountAsync(r => r.Approved, ct);
            var shadowBlockedCount = riskEvaluatedCount - shadowApprovedCount;
            var calendarCutoffStart = now.AddDays(-1);
            var calendarCutoffEnd = now.AddDays(14);
            var upcomingCalendarCount = await db.ScheduledEvents.CountAsync(
                e => e.ScheduledAtUtc >= calendarCutoffStart && e.ScheduledAtUtc <= calendarCutoffEnd, ct);
            var latestDebates = await QueryDebatesAsync(db, limit: 10, ct);

            return Results.Ok(new
            {
                generatedAtUtc = now,
                phase = 3,
                nativeStatus,
                nativeScorerReady = nativeStatus == "ready",
                tradingEnabled = false,
                orderExecutionEnabled = false,
                databaseConfigured = true,
                decisionSwarmConfigured = llmOptions.IsConfigured,
                counts = new
                {
                    events = eventsCount,
                    fastPathPassed = fastPathPassedCount,
                    debates = debatesCount,
                    riskEvaluated = riskEvaluatedCount,
                    riskApproved = shadowApprovedCount,
                    shadowApproved = shadowApprovedCount,
                    shadowBlocked = shadowBlockedCount,
                    cachedQuotes = quotes.Length,
                    quotes = quotes.Length,
                    upcomingCalendar = upcomingCalendarCount,
                    scheduledEvents = upcomingCalendarCount
                },
                latestDebates,
                quotes
            });
        });

        endpoints.MapGet("/api/debates", async (
            int? limit,
            IConfiguration config,
            IDbContextFactory<SentiXDbContext> dbFactory,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var clampedLimit = Math.Clamp(limit ?? 25, 1, 100);
            if (!HasDatabaseConnection(config))
            {
                return Results.Ok(new
                {
                    items = Array.Empty<object>(),
                    databaseConfigured = false
                });
            }

            await using var db = await dbFactory.CreateDbContextAsync(context.RequestAborted);
            var items = await QueryDebatesAsync(db, clampedLimit, context.RequestAborted);
            return Results.Ok(new
            {
                items,
                databaseConfigured = true
            });
        });

        endpoints.MapGet("/api/quotes", (IMarketQuoteCache quoteCache, HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var items = quoteCache.GetAll().Select(ToQuoteDto).ToArray();
            return Results.Ok(new
            {
                items,
                databaseConfigured = true
            });
        });

        endpoints.MapGet("/api/calendar", async (
            IConfiguration config,
            IDbContextFactory<SentiXDbContext> dbFactory,
            TimeProvider clock,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!HasDatabaseConnection(config))
            {
                return Results.Ok(new
                {
                    items = Array.Empty<object>(),
                    databaseConfigured = false
                });
            }

            var now = clock.GetUtcNow();
            var start = now.AddDays(-1);
            var end = now.AddDays(14);
            await using var db = await dbFactory.CreateDbContextAsync(context.RequestAborted);
            var rows = await db.ScheduledEvents.AsNoTracking()
                .Where(e => e.ScheduledAtUtc >= start && e.ScheduledAtUtc <= end)
                .OrderBy(e => e.ScheduledAtUtc)
                .Take(200)
                .ToArrayAsync(context.RequestAborted);

            var items = rows.Select(static e => new
            {
                id = e.Id,
                source = e.Source,
                ticker = e.Ticker,
                eventType = e.EventType,
                title = e.Title,
                scheduledAtUtc = e.ScheduledAtUtc,
                session = (string?)null,
                detailsJson = (string?)null,
                syncedAtUtc = e.SyncedAtUtc
            }).ToArray();

            return Results.Ok(new
            {
                items,
                databaseConfigured = true
            });
        });

        return endpoints;
    }

    private static bool HasDatabaseConnection(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config.GetConnectionString("Default"));

    private static async Task<string> ReadNativeStatusAsync(NativeBridgeClient bridge, CancellationToken ct)
    {
        var check = await bridge.HealthCheckAsync(ct);
        return check.Value switch
        {
            { Status: NativeStatus.Starting } => "starting",
            { Status: NativeStatus.Degraded } or { ModelLoaded: false } => "degraded",
            { Status: NativeStatus.Ready } when !check.IsFailClosed => "ready",
            _ => "unavailable"
        };
    }

    private static async Task<bool> CheckDatabaseReachableAsync(IDbContextFactory<SentiXDbContext> dbFactory, CancellationToken ct)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            return await db.Database.CanConnectAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static object ToQuoteDto(MarketQuoteSnapshot q) => new
    {
        symbol = q.Symbol,
        bidPrice = q.BidPrice,
        askPrice = q.AskPrice,
        midPrice = q.MidPrice,
        spreadBps = q.SpreadBps,
        timestampUtc = q.TimestampUtc
    };

    private static async Task<object[]> QueryDebatesAsync(SentiXDbContext db, int limit, CancellationToken ct)
    {
        var debates = await db.DecisionDebates.AsNoTracking()
            .OrderByDescending(d => d.CreatedAtUtc)
            .Take(limit)
            .ToArrayAsync(ct);

        if (debates.Length == 0)
        {
            return [];
        }

        var eventIds = debates.Select(static d => d.EventId).Distinct().ToArray();
        var proposalIds = debates.Select(static d => d.ProposalId).ToArray();

        var eventsById = await db.MarketEvents.AsNoTracking()
            .Where(e => eventIds.Contains(e.EventId))
            .ToDictionaryAsync(static e => e.EventId, ct);

        var fastPathByEvent = (await db.FastPathResults.AsNoTracking()
                .Where(f => eventIds.Contains(f.EventId))
                .OrderByDescending(f => f.Id)
                .ToArrayAsync(ct))
            .GroupBy(static f => f.EventId)
            .ToDictionary(static g => g.Key, static g => g.First());

        var riskByProposal = (await db.RiskEvaluations.AsNoTracking()
                .Where(r => proposalIds.Contains(r.ProposalId))
                .OrderByDescending(r => r.Id)
                .ToArrayAsync(ct))
            .GroupBy(static r => r.ProposalId)
            .ToDictionary(static g => g.Key, static g => g.First());

        return debates.Select(d =>
        {
            eventsById.TryGetValue(d.EventId, out var ev);
            fastPathByEvent.TryGetValue(d.EventId, out var fp);
            riskByProposal.TryGetValue(d.ProposalId, out var risk);

            return (object)new
            {
                id = d.ProposalId,
                proposalId = d.ProposalId,
                eventId = d.EventId,
                marketEventId = d.EventId,
                createdAtUtc = d.CreatedAtUtc,
                publishedAtUtc = ev?.PublishedAtUtc ?? d.CreatedAtUtc,
                symbol = d.Ticker,
                ticker = d.Ticker,
                source = ev?.Source ?? "UNKNOWN",
                headline = ev?.Headline ?? string.Empty,
                fastPathScore = fp?.CompositeScore ?? 0d,
                promptVersion = d.PromptVersion,
                factJson = d.FactJson,
                bullJson = d.BullJson,
                bearJson = d.BearJson,
                softRiskJson = d.SoftRiskJson,
                proposalJson = d.JudgeJson,
                finalAction = d.FinalAction,
                confidence = d.Confidence,
                tokensUsed = d.TotalTokens,
                latencyMs = d.TotalLatencyMs,
                riskEvaluation = risk is null
                    ? null
                    : new
                    {
                        approved = risk.Approved,
                        tradingState = risk.TradingState,
                        rejectionReason = ExtractRejectionReason(risk.Violations),
                        violations = risk.Violations,
                        checkedAtUtc = risk.EvaluatedAtUtc,
                        calculatedQty = risk.CalculatedQty,
                        clampedSlPct = risk.ClampedSlPct,
                        clampedTpPct = risk.ClampedTpPct
                    }
            };
        }).ToArray();
    }

    private static string? ExtractRejectionReason(string violationsJson)
    {
        if (string.IsNullOrWhiteSpace(violationsJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(violationsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            {
                return null;
            }

            var first = doc.RootElement[0];
            var code = first.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var msg = first.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return code is not null && msg is not null ? $"{code}: {msg}" : msg ?? code;
        }
        catch
        {
            return null;
        }
    }
}
