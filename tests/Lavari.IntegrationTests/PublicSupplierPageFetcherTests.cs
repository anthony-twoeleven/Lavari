using System.Net;
using Lavari.Infrastructure.Integrations;
using Xunit;

namespace Lavari.IntegrationTests;

public sealed class PublicSupplierPageFetcherTests
{
    [Fact]
    public void ExtractProductText_RemovesActiveMarkupAndRetainsProductAndImageDetails()
    {
        const string imageUrl = "https://supplier.example/images/magnus.jpg?width=600";
        const string html = """
            <html><head><title>Ignored page title</title><script>private script content</script></head>
            <body><h1>Magnus Console Table</h1><p>RRP AUD $2,275.00</p>
            <div>Porcelain top &amp; timber frame</div>
            <img src="/images/magnus.jpg?width=600">
            <img src="https://images.example.test/private.jpg?token=secret"></body></html>
            """;

        var text = PublicSupplierPageFetcher.ExtractProductText(html, new Uri("https://supplier.example/products/magnus"));

        Assert.Contains("Magnus Console Table", text);
        Assert.Contains("RRP AUD $2,275.00", text);
        Assert.Contains("Porcelain top & timber frame", text);
        Assert.Contains(imageUrl, text);
        Assert.DoesNotContain("private script content", text);
        Assert.DoesNotContain("Ignored page title", text);
        Assert.DoesNotContain("token=secret", text);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.5", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    public void IsPublicAddress_RejectsPrivateAndSpecialDestinations(string value, bool expected)
        => Assert.Equal(expected, PublicSupplierPageFetcher.IsPublicAddress(IPAddress.Parse(value)));
}
