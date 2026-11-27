namespace SentiX.DecisionSwarm;

/// <summary>
/// Strict JSON Schemas for the five committee roles (Spec 003 §2 "Strict JSON-Schema response mode").
/// Every object declares all properties as <c>required</c> and sets <c>additionalProperties = false</c> so the schemas
/// are accepted by OpenAI / Azure OpenAI <c>strict</c> structured outputs and by Anthropic forced tool input schemas.
/// Range checks ([0, 1], fractions) are still enforced server-side by the orchestrator validators.
/// </summary>
public static class AgentJsonSchemas
{
    public const string FactAnalyst =
        """{"type":"object","properties":{"isActionable":{"type":"boolean"},"eventCategory":{"type":"string","enum":["EARNINGS","GUIDANCE","MACRO_POLICY","M_AND_A","REGULATORY","ANALYST_ACTION","SOCIAL_STATEMENT","NOISE"]},"summary":{"type":"string"},"primaryTicker":{"type":"string"},"instrumentType":{"type":"string","enum":["EQUITY","SECTOR_ETF"]},"affectedTickers":{"type":"array","items":{"type":"string"}},"noveltyScore":{"type":"number"},"reasoning":{"type":"string"}},"required":["isActionable","eventCategory","summary","primaryTicker","instrumentType","affectedTickers","noveltyScore","reasoning"],"additionalProperties":false}""";

    public const string BullAgent =
        """{"type":"object","properties":{"direction":{"type":"string","enum":["LONG","SHORT","NEUTRAL"]},"conviction":{"type":"number"},"keyArguments":{"type":"array","items":{"type":"string"}},"expectedHorizonHours":{"type":"integer"},"priceConfirmationSupported":{"type":"boolean"}},"required":["direction","conviction","keyArguments","expectedHorizonHours","priceConfirmationSupported"],"additionalProperties":false}""";

    public const string BearAgent =
        """{"type":"object","properties":{"counterConviction":{"type":"number"},"keyRisks":{"type":"array","items":{"type":"string"}},"pricedInLikelihood":{"type":"number"},"whipsawRisk":{"type":"string","enum":["LOW","MEDIUM","HIGH"]}},"required":["counterConviction","keyRisks","pricedInLikelihood","whipsawRisk"],"additionalProperties":false}""";

    public const string SoftRiskAgent =
        """{"type":"object","properties":{"softVeto":{"type":"boolean"},"riskLevel":{"type":"string","enum":["LOW","MEDIUM","HIGH","CRITICAL"]},"flags":{"type":"array","items":{"type":"string"}},"recommendation":{"type":"string"}},"required":["softVeto","riskLevel","flags","recommendation"],"additionalProperties":false}""";

    public const string JudgeAgent =
        """{"type":"object","properties":{"proposalId":{"type":"string"},"eventId":{"type":"string"},"action":{"type":"string","enum":["BUY","SELL","HOLD"]},"ticker":{"type":"string"},"instrumentType":{"type":"string","enum":["EQUITY","SECTOR_ETF"]},"confidence":{"type":"number"},"expectedHorizonHours":{"type":"integer"},"suggestedStopLossPct":{"type":"number"},"suggestedTakeProfitPct":{"type":"number"},"rationale":{"type":"string"},"dissentSummary":{"type":"string"}},"required":["proposalId","eventId","action","ticker","instrumentType","confidence","expectedHorizonHours","suggestedStopLossPct","suggestedTakeProfitPct","rationale","dissentSummary"],"additionalProperties":false}""";

    /// <summary>Spec 003 §2 schema for <paramref name="role"/>.</summary>
    public static string For(AgentRole role) => role switch
    {
        AgentRole.FactAnalyst => FactAnalyst,
        AgentRole.BullAgent => BullAgent,
        AgentRole.BearAgent => BearAgent,
        AgentRole.SoftRiskAgent => SoftRiskAgent,
        AgentRole.JudgeAgent => JudgeAgent,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown agent role.")
    };

    /// <summary>
    /// Provider-safe schema / tool name (<c>^[a-zA-Z0-9_-]{1,64}$</c>), e.g. <c>sentix_judge_agent</c>.
    /// </summary>
    public static string NameFor(AgentRole role) => role switch
    {
        AgentRole.FactAnalyst => "sentix_fact_analyst",
        AgentRole.BullAgent => "sentix_bull_agent",
        AgentRole.BearAgent => "sentix_bear_agent",
        AgentRole.SoftRiskAgent => "sentix_soft_risk_agent",
        AgentRole.JudgeAgent => "sentix_judge_agent",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown agent role.")
    };
}
