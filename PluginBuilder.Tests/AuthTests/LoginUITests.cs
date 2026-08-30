using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Microsoft.Playwright.Xunit;
using Xunit;
using Xunit.Abstractions;

namespace PluginBuilder.Tests.AuthTests;

[Collection("Playwright Tests")]
public class LoginPageTests(ITestOutputHelper output) : PageTest
{
    private readonly XUnitLogger _log = new("LoginUITest", output);

    [Fact]
    public async Task Login_Fails_With_InvalidCredentials()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();
        Assert.NotNull(tester.Page);

        await tester.LogIn("wrong-credentials@a.com");
        var errorLocator = tester.Page.Locator(".validation-summary-errors");
        await Expect(errorLocator).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Login_AndProtectedLogout_Succeed()
    {
        await using var tester = new PlaywrightTester(_log);
        tester.Server.ReuseDatabase = false;
        await tester.StartAsync();

        await tester.GoToUrl("/register");
        var email = await tester.RegisterNewUser();
        var page = Assert.IsAssignableFrom<IPage>(tester.Page);
        await Expect(page).ToHaveURLAsync(new Regex(".*/dashboard$", RegexOptions.IgnoreCase));

        var logoutUrl = new Uri(tester.ServerUri!, "/logout").ToString();
        var getResponse = await page.Context.APIRequest.GetAsync(logoutUrl);
        Assert.Equal(405, getResponse.Status);

        var postWithoutTokenResponse = await page.Context.APIRequest.PostAsync(logoutUrl);
        Assert.Equal(400, postWithoutTokenResponse.Status);

        await tester.GoToUrl("/dashboard");
        await Expect(page).ToHaveURLAsync(new Regex(".*/dashboard$", RegexOptions.IgnoreCase));

        await tester.Logout();
        await Expect(page).ToHaveURLAsync(new Regex(".*/login$", RegexOptions.IgnoreCase));

        await tester.GoToUrl("/dashboard");
        Assert.Equal("/login", new Uri(page.Url).AbsolutePath);

        await tester.LogIn(email);
        await Expect(page).ToHaveURLAsync(new Regex(".*/dashboard$", RegexOptions.IgnoreCase));
    }
}
