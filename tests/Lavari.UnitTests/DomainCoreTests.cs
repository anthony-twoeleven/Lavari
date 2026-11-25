using Lavari.Domain.Errors;
using Lavari.Domain.Images;
using Lavari.Domain.Money;
using Lavari.Domain.Security;
using Lavari.Domain.Time;
using Xunit;

namespace Lavari.UnitTests;

public sealed class DomainCoreTests
{
    [Fact]
    public void MoneyCents_Arithmetic_UsesExactCheckedIntegerMath()
    {
        var a = MoneyCents.FromExactDecimalInput(105.55m, "aud");
        var b = MoneyCents.FromExactDecimalInput(44.45m, "AUD");

        Assert.Equal("AUD", a.Currency);
        Assert.Equal(10555L, a.Cents);
        Assert.Equal(15000L, a.Add(b).Cents);
        Assert.Equal(150.00m, a.Add(b).ToHumanDecimal());

        // 10% GST on $80,927.78 (8,092,778 cents) -> 809,277.8 -> half-away-from-zero = 809,278 cents ($8,092.78)
        var subtotal = new MoneyCents(8_092_778L, "AUD");
        var gst = subtotal.ApplyBasisPoints(1000);
        Assert.Equal(809_278L, gst.Cents);
        Assert.Equal(8_902_056L, subtotal.Add(gst).Cents);
    }

    [Fact]
    public void MoneyCents_RejectsSubCentUnsupportedCurrencyAndMismatch()
    {
        var subCentEx = Assert.Throws<ValidationServiceException>(() => MoneyCents.FromExactDecimalInput(10.555m, "AUD"));
        Assert.Equal(ErrorCodes.SubCentAmountNotSupported, subCentEx.ErrorCode);

        var jpyEx = Assert.Throws<ValidationServiceException>(() => new MoneyCents(1000, "JPY"));
        Assert.Equal(ErrorCodes.UnsupportedCurrency, jpyEx.ErrorCode);

        var mismatchEx = Assert.Throws<ValidationServiceException>(() =>
            new MoneyCents(100, "AUD").Add(new MoneyCents(100, "USD")));
        Assert.Equal(ErrorCodes.CurrencyMismatch, mismatchEx.ErrorCode);
    }

    [Fact]
    public void JobTimeZone_ValidatesIanaAndHandlesDstTransitions()
    {
        var sydney = JobTimeZone.Create("Australia/Sydney");
        Assert.Equal("Australia/Sydney", sydney.IanaId);

        Assert.Throws<ValidationServiceException>(() => JobTimeZone.Create("Not/A_Real_Zone"));

        // Sydney springs forward on first Sunday of October 2026 (Oct 4, 2026 02:00 -> 03:00).
        var invalidLocal = new DateTime(2026, 10, 4, 2, 30, 0, DateTimeKind.Unspecified);
        var invalidEx = Assert.Throws<ValidationServiceException>(() => sydney.ConvertLocalToUtc(invalidLocal));
        Assert.Equal(ErrorCodes.AmbiguousOrInvalidLocalTime, invalidEx.ErrorCode);

        // Sydney falls back on first Sunday of April 2026 (Apr 5, 2026 02:00-03:00 occurs twice).
        var ambiguousLocal = new DateTime(2026, 4, 5, 2, 30, 0, DateTimeKind.Unspecified);
        Assert.Throws<ValidationServiceException>(() => sydney.ConvertLocalToUtc(ambiguousLocal));

        var dstUtc = sydney.ConvertLocalToUtc(ambiguousLocal, useDaylightOffsetForAmbiguous: true);
        var stdUtc = sydney.ConvertLocalToUtc(ambiguousLocal, useDaylightOffsetForAmbiguous: false);
        Assert.Equal(TimeSpan.FromHours(1), stdUtc - dstUtc);
    }

    [Fact]
    public void PermissionRegistry_ComputesUnionFullAccessAndDeniesUnknownOrDisabled()
    {
        var readRole = new RoleGrantSnapshot(
            Guid.NewGuid(), "Viewer", false,
            new Dictionary<string, AccessLevel> { [ModuleKeys.Jobs] = AccessLevel.Read });
        var alterRole = new RoleGrantSnapshot(
            Guid.NewGuid(), "Designer", false,
            new Dictionary<string, AccessLevel> { [ModuleKeys.Jobs] = AccessLevel.Alter, [ModuleKeys.Quotes] = AccessLevel.Read });

        var effective = PermissionRegistry.ComputeEffectiveGrants(true, [readRole, alterRole]);
        Assert.True(PermissionRegistry.Satisfies(effective, ModuleKeys.Jobs, AccessLevel.Alter));
        Assert.False(PermissionRegistry.Satisfies(effective, ModuleKeys.Jobs, AccessLevel.Delete));
        Assert.True(PermissionRegistry.Satisfies(effective, ModuleKeys.Quotes, AccessLevel.Read));
        Assert.False(PermissionRegistry.Satisfies(effective, ModuleKeys.Invoices, AccessLevel.Read));
        Assert.False(PermissionRegistry.Satisfies(effective, "unregistered_module", AccessLevel.Read));

        var disabled = PermissionRegistry.ComputeEffectiveGrants(false, [alterRole]);
        Assert.False(PermissionRegistry.Satisfies(disabled, ModuleKeys.Jobs, AccessLevel.Read));

        var fullAccess = new RoleGrantSnapshot(Guid.NewGuid(), "Full Access", true, new Dictionary<string, AccessLevel>());
        var adminGrants = PermissionRegistry.ComputeEffectiveGrants(true, [fullAccess]);
        foreach (var module in ModuleKeys.All)
        {
            Assert.True(PermissionRegistry.Satisfies(adminGrants, module, AccessLevel.Delete));
        }
    }

    [Theory]
    [InlineData(4000, 3000, 1440, 1080)]
    [InlineData(3000, 4000, 1080, 1440)]
    [InlineData(640, 480, 640, 480)]
    [InlineData(4000, 4000, 1080, 1080)]
    [InlineData(5000, 1000, 1920, 384)]
    public void ImageResizePolicy_MatchesExact1080pSpecBounds(int inW, int inH, int expW, int expH)
    {
        var result = ImageResizePolicy.CalculateMax1080pSize(inW, inH);
        Assert.Equal(expW, result.Width);
        Assert.Equal(expH, result.Height);
    }
}
