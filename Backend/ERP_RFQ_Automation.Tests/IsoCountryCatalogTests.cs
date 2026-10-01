using ERP_RFQ_Automation.Platform.Services;

namespace ERP_RFQ_Automation.Tests;

public sealed class IsoCountryCatalogTests
{
    [Fact]
    public void V1_is_a_complete_unique_iso_3166_alpha2_catalog()
    {
        var countries = IsoCountryCatalogV1.Countries;

        Assert.Equal(249, countries.Count);
        Assert.Equal(249, countries.Select(country => country.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(249, countries.Select(country => country.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(countries, country =>
        {
            Assert.Matches("^[A-Z]{2}$", country.Code);
            Assert.False(string.IsNullOrWhiteSpace(country.Name));
        });

        Assert.Contains(countries, country => country is { Code: "AE", Name: "United Arab Emirates" });
        Assert.Contains(countries, country => country is { Code: "PK", Name: "Pakistan" });
        Assert.Contains(countries, country => country is { Code: "SA", Name: "Saudi Arabia" });
        Assert.Contains(countries, country => country is { Code: "US", Name: "United States" });
    }
}
