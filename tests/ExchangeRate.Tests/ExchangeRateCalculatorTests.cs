using ExchangeRate.Core;
using ExchangeRate.Core.Entities;
using ExchangeRate.Core.Enums;
using ExchangeRate.Core.Interfaces.Providers;
using FluentAssertions;
using Moq;
using Xunit;

namespace ExchangeRate.Tests;

/// <summary>
/// Unit tests for ExchangeRateCalculator — the pure domain logic extracted in Step 4.
/// These tests exercise calculation paths directly without HTTP, caching, or data store involvement.
/// </summary>
public class ExchangeRateCalculatorTests
{
    private static readonly DateTime Date = new(2024, 01, 15);
    private static readonly DateTime MinFxDate = new(2024, 01, 10);

    #region Same-Currency Tests

    [Fact]
    public void GetFxRate_SameCurrency_ReturnsOne()
    {
        var provider = CreateProvider(CurrencyTypes.EUR, QuoteTypes.Indirect);
        var rates = new Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>>();
        var pegged = new Dictionary<CurrencyTypes, PeggedCurrency>();

        var result = ExchangeRateCalculator.GetFxRate(
            rates, pegged, Date, MinFxDate, provider, CurrencyTypes.USD, CurrencyTypes.USD, out var lookupCurrency);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1m);
        lookupCurrency.Should().Be(CurrencyTypes.USD);
    }

    #endregion

    #region Quote Type Inversion Tests

    [Fact]
    public void GetFxRate_IndirectQuote_FromProviderCurrency_ReturnsRawRate()
    {
        // ECB: Indirect, EUR-based. EUR→USD should return stored rate directly.
        var provider = CreateProvider(CurrencyTypes.EUR, QuoteTypes.Indirect);
        var rates = BuildRates(CurrencyTypes.USD, Date, 1.0856m);
        var pegged = new Dictionary<CurrencyTypes, PeggedCurrency>();

        var result = ExchangeRateCalculator.GetFxRate(
            rates, pegged, Date, MinFxDate, provider, CurrencyTypes.EUR, CurrencyTypes.USD, out _);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1.0856m);
    }

    [Fact]
    public void GetFxRate_IndirectQuote_ToProviderCurrency_ReturnsInverse()
    {
        // ECB: Indirect, EUR-based. USD→EUR should return 1/rate.
        var provider = CreateProvider(CurrencyTypes.EUR, QuoteTypes.Indirect);
        var rates = BuildRates(CurrencyTypes.USD, Date, 1.0856m);
        var pegged = new Dictionary<CurrencyTypes, PeggedCurrency>();

        var result = ExchangeRateCalculator.GetFxRate(
            rates, pegged, Date, MinFxDate, provider, CurrencyTypes.USD, CurrencyTypes.EUR, out _);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeApproximately(1 / 1.0856m, 0.00001m);
    }

    [Fact]
    public void GetFxRate_DirectQuote_ToProviderCurrency_ReturnsRawRate()
    {
        // MXCB: Direct, MXN-based. USD→MXN should return stored rate directly.
        var provider = CreateProvider(CurrencyTypes.MXN, QuoteTypes.Direct);
        var rates = BuildRates(CurrencyTypes.USD, Date, 17.5m);
        var pegged = new Dictionary<CurrencyTypes, PeggedCurrency>();

        var result = ExchangeRateCalculator.GetFxRate(
            rates, pegged, Date, MinFxDate, provider, CurrencyTypes.USD, CurrencyTypes.MXN, out _);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(17.5m);
    }

    [Fact]
    public void GetFxRate_DirectQuote_FromProviderCurrency_ReturnsInverse()
    {
        // MXCB: Direct, MXN-based. MXN→USD should return 1/rate.
        var provider = CreateProvider(CurrencyTypes.MXN, QuoteTypes.Direct);
        var rates = BuildRates(CurrencyTypes.USD, Date, 17.5m);
        var pegged = new Dictionary<CurrencyTypes, PeggedCurrency>();

        var result = ExchangeRateCalculator.GetFxRate(
            rates, pegged, Date, MinFxDate, provider, CurrencyTypes.MXN, CurrencyTypes.USD, out _);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeApproximately(1 / 17.5m, 0.00001m);
    }

    #endregion

    #region Date Fallback Tests

    [Fact]
    public void GetFxRate_ExactDateMissing_FallsBackToEarlierDate()
    {
        var provider = CreateProvider(CurrencyTypes.EUR, QuoteTypes.Indirect);
        var earlierDate = Date.AddDays(-2);
        var rates = BuildRates(CurrencyTypes.USD, earlierDate, 1.09m);
        var pegged = new Dictionary<CurrencyTypes, PeggedCurrency>();

        var result = ExchangeRateCalculator.GetFxRate(
            rates, pegged, Date, MinFxDate, provider, CurrencyTypes.EUR, CurrencyTypes.USD, out _);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1.09m);
    }

    [Fact]
    public void GetFxRate_RateExistsOneDayBeforeMinFxDate_ReturnsNotFound()
    {
        var provider = CreateProvider(CurrencyTypes.EUR, QuoteTypes.Indirect);
        var beforeMin = MinFxDate.AddDays(-1);
        var rates = BuildRates(CurrencyTypes.USD, beforeMin, 1.09m);
        var pegged = new Dictionary<CurrencyTypes, PeggedCurrency>();

        var result = ExchangeRateCalculator.GetFxRate(
            rates, pegged, Date, MinFxDate, provider, CurrencyTypes.EUR, CurrencyTypes.USD, out _);

        result.IsFailed.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Message.Contains("No fx rate found"));
    }

    #endregion

    #region Error Path Tests

    [Fact]
    public void GetFxRate_CurrencyNotInRatesAndNotPegged_ReturnsNotSupportedError()
    {
        var provider = CreateProvider(CurrencyTypes.EUR, QuoteTypes.Indirect);
        var rates = new Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>>();
        var pegged = new Dictionary<CurrencyTypes, PeggedCurrency>();

        var result = ExchangeRateCalculator.GetFxRate(
            rates, pegged, Date, MinFxDate, provider, CurrencyTypes.EUR, CurrencyTypes.ZAR, out _);

        result.IsFailed.Should().BeTrue();
        result.Errors.Should().ContainSingle(e => e.Message.Contains("Not supported currency"));
    }

    #endregion

    #region Pegged Currency Tests

    [Fact]
    public void GetFxRate_PeggedCurrency_ResolvesViaAnchor()
    {
        // AED pegged to USD at 0.27229. Provider is EUR (Indirect).
        // EUR→AED: need EUR→USD rate, then apply peg.
        var provider = CreateProvider(CurrencyTypes.EUR, QuoteTypes.Indirect);
        var rates = BuildRates(CurrencyTypes.USD, Date, 1.10m);
        var pegged = new Dictionary<CurrencyTypes, PeggedCurrency>
        {
            {
                CurrencyTypes.AED, new PeggedCurrency
                {
                    CurrencyId = CurrencyTypes.AED,
                    PeggedTo = CurrencyTypes.USD,
                    Rate = 0.27229m
                }
            }
        };

        var result = ExchangeRateCalculator.GetFxRate(
            rates, pegged, Date, MinFxDate, provider, CurrencyTypes.EUR, CurrencyTypes.AED, out _);

        result.IsSuccess.Should().BeTrue();
        // EUR→AED = usdRate / pegRate = 1.10 / 0.27229
        result.Value.Should().BeApproximately(1.10m / 0.27229m, 0.001m);
    }

    #endregion

    #region Helpers

    private static IExchangeRateProvider CreateProvider(CurrencyTypes currency, QuoteTypes quoteType)
    {
        var mock = new Mock<IExchangeRateProvider>();
        mock.Setup(p => p.Currency).Returns(currency);
        mock.Setup(p => p.QuoteType).Returns(quoteType);
        return mock.Object;
    }

    private static Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>> BuildRates(
        CurrencyTypes currency, DateTime date, decimal rate)
    {
        return new Dictionary<CurrencyTypes, Dictionary<DateTime, decimal>>
        {
            { currency, new Dictionary<DateTime, decimal> { { date, rate } } }
        };
    }

    #endregion
}
