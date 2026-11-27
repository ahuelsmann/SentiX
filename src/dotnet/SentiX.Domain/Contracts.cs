using System.Text.Json;
using System.Text.Json.Serialization;

namespace SentiX.Domain;

public static class SentiXJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
            NumberHandling = JsonNumberHandling.Strict
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false));
        options.Converters.Add(new UtcTimestampConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var value)
                ? value.ToUniversalTime()
                : throw new JsonException("Expected an ISO 8601 timestamp.");

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime);
    }
}

public enum EventSource { AlpacaBenzinga, SecEdgar, XAllowlist }
public enum TrustTier { Primary, VerifiedSocial }
public enum NativeStatus { Starting, Ready, Degraded }
public enum IpcMessageType { HealthCheck, HealthCheckAck, ReloadConfig, ReloadConfigAck, ScoreEvent, ScoreEventResult, Error }
public enum IpcErrorCode { InvalidFrame, PayloadTooLarge, DeserializationError, UnsupportedVersion, ModelNotReady, InferenceTimeout, InternalError }
public enum DropReason { SourceNotAllowed, Duplicate, NoWatchlistMatch, LowSentimentStrength }

public sealed record MarketEvent(
    string EventId, EventSource Source, string Author, DateTimeOffset PublishedAtUtc,
    string Headline, string Summary, string Body, string[] SourceTickers);

public sealed record IpcEnvelope<T>(
    int ProtocolVersion, Guid CorrelationId, IpcMessageType Type, DateTimeOffset TimestampUtc, T Payload);

public sealed record NativeHealth(
    NativeStatus Status, string EngineVersion, bool ModelLoaded, long UptimeSeconds, ulong ProcessedEventsTotal);

public sealed record ConfigReload(string Status, int ConfigVersion, int WatchlistCount, int SectorEtfCount, int SocialAllowlistCount);
public sealed record IpcError(IpcErrorCode Code, string Message, bool Retryable);
public sealed record FinbertScores(double Positive, double Negative, double Neutral, double CompositeScore);

public sealed record FastPathResult(
    string EventId, bool IsDuplicate, string DedupHash, string[] MatchedTickers, string[] MatchedSectorEtfs,
    FinbertScores Finbert, bool PassToDecisionSwarm, DropReason? DropReason, int ProcessingTimeUs);

public enum TradeAction { Hold, Buy, Sell }
public enum InstrumentType { Equity, SectorEtf }
public enum TradingState { Active, HaltedDailyLoss, HaltedManual, HaltedSystemError }
public enum EventCategory
{
    Earnings, Guidance, MacroPolicy,
    [JsonStringEnumMemberName("M_AND_A")] MergersAndAcquisitions,
    Regulatory, AnalystAction, SocialStatement, Noise
}
public enum TradeDirection { Long, Short, Neutral }
public enum WhipsawRisk { Low, Medium, High }
public enum RiskLevel { Low, Medium, High, Critical }
public enum ScheduledEventType { Earnings, Fomc, Cpi, Nfp, Macro }
public enum CalendarSource { Finnhub, OfficialFeed, StaticCalendar }

public sealed record FactAnalysis(
    bool IsActionable, EventCategory EventCategory, string Summary, string PrimaryTicker, InstrumentType InstrumentType,
    string[] AffectedTickers, double NoveltyScore, string Reasoning);

public sealed record BullAnalysis(
    TradeDirection Direction, double Conviction, string[] KeyArguments, int ExpectedHorizonHours,
    bool PriceConfirmationSupported);

public sealed record BearAnalysis(
    double CounterConviction, string[] KeyRisks, double PricedInLikelihood, WhipsawRisk WhipsawRisk);

public sealed record SoftRiskAnalysis(bool SoftVeto, RiskLevel RiskLevel, string[] Flags, string Recommendation);

public sealed record TradeProposal(
    string ProposalId, string EventId, TradeAction Action, string Ticker, InstrumentType InstrumentType,
    double Confidence, int ExpectedHorizonHours, decimal SuggestedStopLossPct, decimal SuggestedTakeProfitPct,
    string Rationale, string DissentSummary);
