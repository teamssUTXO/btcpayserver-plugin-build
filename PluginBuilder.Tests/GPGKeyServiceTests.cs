using System.ComponentModel.DataAnnotations;
using PluginBuilder.DataModels;
using PluginBuilder.Services;
using PluginBuilder.Tests.TestData;
using PluginBuilder.Util.Extensions;
using PluginBuilder.ViewModels;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests;

public class GPGKeyServiceTests(ITestOutputHelper logs) : UnitTestBase(logs)
{
    private const int ExpectedMaxArmouredPublicKeyLength = 256 * 1024;

    [Fact]
    public void ValidateArmouredPublicKeyAcceptsKeyAtSizeLimit()
    {
        Assert.Equal(ExpectedMaxArmouredPublicKeyLength, PgpKeyViewModel.MaxArmouredPublicKeyLength);
        var publicKey = GpgTestData.SamplePublicKey.PadLeft(ExpectedMaxArmouredPublicKeyLength);
        Assert.Equal(ExpectedMaxArmouredPublicKeyLength, publicKey.Length);

        var service = new GPGKeyService(null!);
        var valid = service.ValidateArmouredPublicKey(publicKey, out var message, out var key);

        Assert.True(valid, message);
        Assert.NotNull(key);
        Assert.Equal("4C6A315E0BEF6D464BD747EFF794D1D2212EFC48", key.Fingerprint);
    }

    [Fact]
    public void ValidateArmouredPublicKeyRejectsKeyJustOverSizeLimitBeforeTrimming()
    {
        var publicKey = GpgTestData.SamplePublicKey.PadLeft(ExpectedMaxArmouredPublicKeyLength + 1);
        Assert.Equal(ExpectedMaxArmouredPublicKeyLength + 1, publicKey.Length);

        var service = new GPGKeyService(null!);
        var valid = service.ValidateArmouredPublicKey(publicKey, out var message, out var key);

        Assert.False(valid);
        Assert.Null(key);
        Assert.Equal(PgpKeyViewModel.PublicKeyTooLargeError, message);
    }

    [Fact]
    public void PublicKeyModelRejectsOversizedValue()
    {
        var model = new PgpKeyViewModel
        {
            PublicKey = new string('A', PgpKeyViewModel.MaxArmouredPublicKeyLength + 1)
        };
        var results = new List<ValidationResult>();

        var valid = Validator.TryValidateObject(model, new ValidationContext(model), results, true);

        Assert.False(valid);
        var result = Assert.Single(results);
        Assert.Equal(PgpKeyViewModel.PublicKeyTooLargeError, result.ErrorMessage);
        Assert.Contains(nameof(PgpKeyViewModel.PublicKey), result.MemberNames);
    }

    [Fact]
    public async Task VerifyDetachedSignatureRejectsOversizedStoredKey()
    {
        await using var tester = Create();
        tester.ReuseDatabase = false;
        await tester.Start();

        var userId = await tester.CreateFakeUserAsync();
        const string pluginSlug = "oversized-gpg-key";
        await using var conn = await tester.GetService<DBConnectionFactory>().Open();
        Assert.True(await conn.NewPlugin(pluginSlug, userId));
        await conn.SetAccountDetailSettings(new AccountSettings
        {
            GPGKey = new PgpKeyViewModel
            {
                PublicKey = new string('A', PgpKeyViewModel.MaxArmouredPublicKeyLength + 1)
            }
        }, userId);

        var result = await tester.GetService<GPGKeyService>()
            .VerifyDetachedSignature(pluginSlug, userId, [], [0]);

        Assert.False(result.valid);
        Assert.Equal(PgpKeyViewModel.PublicKeyTooLargeError, result.message);
    }
}
