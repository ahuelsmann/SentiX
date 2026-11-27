namespace SentiX.Domain;

public sealed record MarketQuoteSnapshot(string Symbol, decimal BidPrice, decimal AskPrice, decimal MidPrice,
    double SpreadBps, DateTimeOffset TimestampUtc);

public interface IMarketQuoteCache
{
    bool TryGet(string symbol, out MarketQuoteSnapshot? snapshot);
    void Update(MarketQuoteSnapshot snapshot);
    IReadOnlyCollection<MarketQuoteSnapshot> GetAll() => [];
}
