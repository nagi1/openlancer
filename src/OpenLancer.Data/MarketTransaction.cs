namespace OpenLancer.Data;

/// <summary>
/// The transaction type of a <c>marketgood</c> line, its sixth value: what the base does with the good. Retail reads
/// the value as a whole number.
/// </summary>
public enum MarketTransaction
{
    /// <summary>0: the base buys and sells the good.</summary>
    BuyAndSell = 0,

    /// <summary>1: the base only buys the good. The player can sell the good there, but cannot buy it.</summary>
    BuyOnly = 1,

    /// <summary>2: the base only sells the good. The player can buy the good there, but cannot sell it.</summary>
    SellOnly = 2,
}
