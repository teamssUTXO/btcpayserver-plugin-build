using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using PluginBuilder.Services;
using PluginBuilder.Util.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.PluginTests;

[Collection("Playwright Tests")]
public class PluginTenantIsolationUITests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("PluginTenantIsolationUITests", output);

    [Fact]
    public async Task FormPluginSlugCannotOverrideAuthorizedRoutePlugin()
    {
        await using var tester = new PlaywrightTester(_log) { Server = { ReuseDatabase = false } };
        await tester.StartAsync();
        await using var conn = await tester.Server.GetService<DBConnectionFactory>().Open();

        var attackerEmail = $"attacker-{Guid.NewGuid():N}@test.com";
        var victimEmail = $"victim-{Guid.NewGuid():N}@test.com";
        var attackerId = await tester.Server.CreateFakeUserAsync(attackerEmail);
        var victimId = await tester.Server.CreateFakeUserAsync(victimEmail);
        var attackerSlug = "attacker-" + PlaywrightTester.GetRandomUInt256()[..8];
        var victimSlug = "victim-" + PlaywrightTester.GetRandomUInt256()[..8];
        const string attackerRepositoryBefore = "https://github.com/example/attacker-before";
        const string attackerRepositoryAfter = "https://github.com/example/attacker-after";
        const string victimRepository = "https://github.com/example/victim";

        await conn.NewPlugin(attackerSlug, attackerId);
        await conn.NewPlugin(victimSlug, victimId);
        await conn.SetPluginSettings(attackerSlug, new PluginSettings
        {
            PluginTitle = attackerSlug,
            Description = "Attacker plugin",
            GitRepository = attackerRepositoryBefore
        });
        await conn.SetPluginSettings(victimSlug, new PluginSettings
        {
            PluginTitle = victimSlug,
            Description = "Victim plugin",
            GitRepository = victimRepository
        });

        await tester.LogIn(attackerEmail);
        await tester.GoToUrl($"/plugins/{attackerSlug}/settings");
        var page = Assert.IsAssignableFrom<IPage>(tester.Page);
        var form = page.Locator("#plugin-setting-form");
        await Expect(form.Locator("input[name='__RequestVerificationToken']")).ToHaveCountAsync(1);
        await page.FillAsync("#PluginTitle", attackerSlug);
        await page.FillAsync("#Description", "Attacker plugin");
        await page.FillAsync("#GitRepository", attackerRepositoryAfter);
        await page.EvaluateAsync(
            """
            victimSlug => {
                const input = document.createElement('input');
                input.type = 'hidden';
                input.name = 'pluginSlug';
                input.value = victimSlug;
                document.querySelector('#plugin-setting-form').appendChild(input);
            }
            """,
            victimSlug);

        await Expect(form.Locator("input[name='pluginSlug']")).ToHaveValueAsync(victimSlug);
        await page.Locator("button[type='submit'][form='plugin-setting-form']").ClickAsync();

        await Expect(page).ToHaveURLAsync(new Regex(
            $"/plugins/{Regex.Escape(attackerSlug)}/settings$",
            RegexOptions.IgnoreCase));
        await tester.AssertNoError();
        Assert.Equal(attackerRepositoryAfter, (await conn.GetSettings(attackerSlug))?.GitRepository);
        Assert.Equal(victimRepository, (await conn.GetSettings(victimSlug))?.GitRepository);
    }
}
