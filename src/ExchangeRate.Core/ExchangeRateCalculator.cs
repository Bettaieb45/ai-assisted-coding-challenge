using FluentResults;
using ExchangeRate.Core.Entities;
using ExchangeRate.Core.Enums;
using ExchangeRate.Core.Interfaces.Providers;

namespace ExchangeRate.Core
{
    /// <summary>
    /// Pure domain logic for exchange rate calculation.
    /// Handles quote type inversion, pegged currency resolution, and date fallback.
    /// Has no state, no I/O, and no side effects.
    /// </summary>
    internal static class ExchangeRateCalculator
    {
        internal static Result<decimal> GetFxRate(
            IReadOnlyDictionary<CurrencyTypes, Dictionary<DateTime, decimal>> ratesByCurrencyAndDate,
            IReadOnlyDictionary<CurrencyTypes, PeggedCurrency> peggedCurrencies,
            DateTime date,
            DateTime minFxDate,
            IExchangeRateProvider provider,
            CurrencyTypes fromCurrency,
            CurrencyTypes toCurrency,
            out CurrencyTypes lookupCurrency)
        {
                // Handle same-currency conversion (can happen in recursive pegged currency lookups)
                if (fromCurrency == toCurrency)
                {
                    lookupCurrency = fromCurrency;
                    return Result.Ok(1m);
                }

                //  always need to find the rate for the currency that is not the provider's currency
                lookupCurrency = toCurrency == provider.Currency ? fromCurrency : toCurrency;
                var nonLookupCurrency = toCurrency == provider.Currency ? toCurrency : fromCurrency;

                if (!ratesByCurrencyAndDate.TryGetValue(lookupCurrency, out var currencyDict))
                {
                    if (!peggedCurrencies.TryGetValue(lookupCurrency, out var peggedCurrency))
                    {
                        return Result.Fail(new NotSupportedCurrencyError(lookupCurrency));
                    }

                    var peggedToCurrencyResult = GetFxRate(ratesByCurrencyAndDate, peggedCurrencies, date, minFxDate, provider, nonLookupCurrency, peggedCurrency.PeggedTo!.Value, out _);

                    if (peggedToCurrencyResult.IsFailed)
                    {
                        return peggedToCurrencyResult;
                    }

                    var peggedRate = peggedCurrency.Rate!.Value;
                    var resultRate = peggedToCurrencyResult.Value;

                    return Result.Ok(toCurrency == provider.Currency
                        ? peggedRate / resultRate
                        : resultRate / peggedRate);

                }
                // start looking for the date, and decreasing the date if no match found (but only until the minFxDate)

            for (var d = date; d >= minFxDate; d = d.AddDays(-1d))
            {
                if (currencyDict.TryGetValue(d, out var fxRate))
                {
                    /*
                       If your local currency is EUR:
                       - Direct exchange rate: 1 USD = 0.92819 EUR
                       - Indirect exchange rate: 1 EUR = 1.08238 USD
                    */

                    // QuoteType    ProviderCurrency    FromCurrency    ToCurrency    Rate
                    // Direct       EUR                 USD             EUR           fxRate
                    // Direct       EUR                 EUR             USD           1/fxRate
                    // InDirect     EUR                 USD             EUR           1/fxRate
                    // InDirect     EUR                 EUR             USD           fxRate

                    return provider.QuoteType switch
                    {
                        QuoteTypes.Direct when toCurrency == provider.Currency => Result.Ok(fxRate),
                        QuoteTypes.Direct when fromCurrency == provider.Currency => Result.Ok(1 / fxRate),
                        QuoteTypes.Indirect when fromCurrency == provider.Currency => Result.Ok(fxRate),
                        QuoteTypes.Indirect when toCurrency == provider.Currency => Result.Ok(1 / fxRate),
                        _ => throw new InvalidOperationException("Unsupported QuoteType")
                    };
                }
            }

            return Result.Fail(new NoFxRateFoundError());
        }
    }

    class NotSupportedCurrencyError : Error
    {
        public NotSupportedCurrencyError(CurrencyTypes currency)
            : base("Not supported currency: " + currency) { }
    }

    class NoFxRateFoundError : Error
    {
        public NoFxRateFoundError()
            : base("No fx rate found") { }
    }
}
