using Microsoft.Playwright;
using OokiGrader.SchoolManager.Delivery;

namespace OokiGrader.SchoolManager.Tests;

// Offline DOM regression fixtures based on the observed 2026-09-19 UI.
// Every network request is intercepted; these tests never contact School Manager.
public sealed class SchoolManagerBrowserContractTests
{
    [EdgeFact]
    public async Task StartsFromAddActionBeforeSelectingAnExactStudent()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge" });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/*", route => route.FulfillAsync(new()
        {
            ContentType = "text/html; charset=utf-8",
            Body = """
                <ion-fab-button role="button" tabindex="0">+</ion-fab-button>
                <input type="search">
                <div class="table-row">
                  <div class="table-data-1"><div>試験 太郎</div><div>9999</div></div>
                  <div class="table-data-2">9999</div><ion-button role="button">選択</ion-button>
                </div>
                <script>
                  let started = false;
                  document.querySelector('ion-fab-button').onclick = () => {
                    started = true; history.pushState({}, '', '/message-thread-target-users');
                  };
                  document.querySelector('.table-row ion-button').onclick = () => {
                    if (started) history.pushState({}, '', '/message-thread-members-and-title');
                  };
                </script>
                """,
        }));
        await PlaywrightSchoolManagerClient.SelectExactStudentAsync(page, new(
            new Uri("https://fsm.flens.jp/"), new("unused", "unused"), "9999", "試験 太郎",
            "試験", "試験", "test.pdf", "%PDF-1.7"u8.ToArray(), true), default);
        Assert.EndsWith("/message-thread-members-and-title", page.Url);
        Assert.Equal("9999", await page.GetByRole(AriaRole.Searchbox).InputValueAsync());
    }

    [EdgeFact]
    public async Task AllowsDisabledStudentPlaceholderAndRejectsUnsafeRecipientStates()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge" });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/*", route => route.AbortAsync());

        foreach (var placeholders in new[] { false, true })
        {
            await page.SetContentAsync(GuardianForm(placeholders));
            await PlaywrightSchoolManagerClient.VerifyGuardianOnlyAndSetTitleAsync(page, "試験件名");
            Assert.Equal("試験件名", await page.Locator("textarea").InputValueAsync());

            foreach (var mutation in new[]
            {
                "document.querySelector('[formarrayname=students] ion-checkbox').checked = true",
                "document.querySelector('[formcontrolname=parent]').checked = false",
                "document.querySelector('[formarrayname=students]').remove()",
                "document.querySelector('[formarrayname=parents]').remove()",
                "document.querySelector('[formarrayname=students]').append(document.querySelector('[formarrayname=students] ion-checkbox').cloneNode(true))",
            })
            {
                await page.SetContentAsync(GuardianForm(placeholders));
                await page.EvaluateAsync(mutation);
                await Assert.ThrowsAsync<SchoolManagerClientException>(() =>
                    PlaywrightSchoolManagerClient.VerifyGuardianOnlyAndSetTitleAsync(page, "試験件名"));
                Assert.Equal("", await page.Locator("textarea").InputValueAsync());
            }
        }

        await page.SetContentAsync(GuardianForm(true));
        await page.EvaluateAsync("document.querySelector('[formarrayname=students] input').removeAttribute('disabled')");
        await Assert.ThrowsAsync<SchoolManagerClientException>(() =>
            PlaywrightSchoolManagerClient.VerifyGuardianOnlyAndSetTitleAsync(page, "試験件名"));
    }

    [EdgeFact]
    public async Task FindsOnlyNormalSendAndNeverClicksItDuringReadinessCheck()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Channel = "msedge" });
        var page = await browser.NewPageAsync();
        await page.RouteAsync("**/*", route => route.AbortAsync());
        await page.SetContentAsync("""
            <ion-button role="button" class="send-message-button" onclick="document.body.dataset.sent='true'">➤</ion-button>
            <ion-button role="button" class="send-and-resolve-button">送信後に解決済にする</ion-button>
            """);
        var send = await PlaywrightSchoolManagerClient.GetSendControlAsync(page);
        Assert.Equal("send-message-button", await send.GetAttributeAsync("class"));
        Assert.Null(await page.Locator("body").GetAttributeAsync("data-sent"));
        await page.EvaluateAsync("document.querySelector('.send-message-button').setAttribute('aria-disabled','true')");
        await Assert.ThrowsAsync<SchoolManagerClientException>(() => PlaywrightSchoolManagerClient.GetSendControlAsync(page));
        await page.EvaluateAsync("document.querySelector('.send-message-button').removeAttribute('aria-disabled'); document.body.append(document.querySelector('.send-message-button').cloneNode(true))");
        await Assert.ThrowsAsync<SchoolManagerClientException>(() => PlaywrightSchoolManagerClient.GetSendControlAsync(page));
        Assert.Null(await page.Locator("body").GetAttributeAsync("data-sent"));
    }

    private static string GuardianForm(bool placeholders)
    {
        string Student(string control) => placeholders
            ? "<ion-checkbox aria-checked='false' class='checkbox-disabled'><input class='aux-input' disabled></ion-checkbox>"
            : $"<ion-checkbox formcontrolname='{control}'></ion-checkbox>";
        return $$"""
            <ion-checkbox formcontrolname="parentsAll"></ion-checkbox>
            <div formarrayname="parents"><ion-checkbox formcontrolname="parent"></ion-checkbox></div>
            <div class="captioned-checkbox">{{Student("studentsAll")}}<div class="checkbox-caption">生徒</div></div>
            <div formarrayname="students">{{Student("student")}}</div>
            <ion-textarea formcontrolname="title"><textarea></textarea></ion-textarea><button>次へ</button>
            <script>
              document.querySelectorAll('ion-checkbox').forEach(e => e.checked = false);
              document.querySelector('[formcontrolname=parentsAll]').checked = true;
              document.querySelector('[formcontrolname=parent]').checked = { id: 'test-parent' };
            </script>
            """;
    }
}

public sealed class EdgeFactAttribute : FactAttribute
{
    public EdgeFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "Edge", "Application", "msedge.exe")))
        {
            Skip = "Offline browser contract tests require Windows and Microsoft Edge.";
        }
    }
}
