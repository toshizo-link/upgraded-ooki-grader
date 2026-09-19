using System.Text.Json;
using Microsoft.Playwright;

namespace OokiGrader.SchoolManager.Delivery;

public sealed record SchoolManagerCredentials(string Username, string Password);

public sealed record SchoolManagerDeliveryRequest(
    Uri BaseUri,
    SchoolManagerCredentials Credentials,
    string StudentNumber,
    string StudentDisplayName,
    string Title,
    string Body,
    string AttachmentName,
    byte[] AttachmentBytes,
    bool DryRun);

public sealed record SchoolManagerDeliveryResult(bool DryRun, string? ThreadId);

public sealed class SchoolManagerClientException(
    string code,
    string message,
    bool outcomeUnknown = false,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
    public bool OutcomeUnknown { get; } = outcomeUnknown;
}

public interface ISchoolManagerClient
{
    Task<SchoolManagerDeliveryResult> DeliverAsync(
        SchoolManagerDeliveryRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class PlaywrightSchoolManagerClient : ISchoolManagerClient
{
    private const int MaximumAttachmentBytes = 25 * 1024 * 1024;

    public async Task<SchoolManagerDeliveryResult> DeliverAsync(
        SchoolManagerDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        var finalRequestStarted = false;
        try
        {
            using var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
            await using var browser = await playwright.Chromium.LaunchAsync(
                new BrowserTypeLaunchOptions
                {
                    Channel = "msedge",
                    Headless = true,
                    Timeout = 30_000,
                }).ConfigureAwait(false);
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                AcceptDownloads = false,
                IgnoreHTTPSErrors = false,
                Locale = "ja-JP",
            }).ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);
            await LoginAsync(page, request, cancellationToken).ConfigureAwait(false);
            await SelectExactStudentAsync(page, request, cancellationToken)
                .ConfigureAwait(false);
            await VerifyGuardianOnlyAndSetTitleAsync(page, request.Title)
                .ConfigureAwait(false);

            if (request.DryRun)
            {
                return new SchoolManagerDeliveryResult(true, null);
            }

            await page.GetByRole(AriaRole.Button, new() { Name = "次へ" })
                .ClickAsync(new LocatorClickOptions { Timeout = 15_000 })
                .ConfigureAwait(false);
            await page.WaitForURLAsync(
                    "**/message-thread-details/0/create",
                    new PageWaitForURLOptions { Timeout = 15_000 })
                .ConfigureAwait(false);

            var fileInput = page.Locator("input[type='file']").First;
            var uploadResponse = await page.RunAndWaitForResponseAsync(
                    () => fileInput.SetInputFilesAsync(new FilePayload
                    {
                        Name = request.AttachmentName,
                        MimeType = "application/pdf",
                        Buffer = request.AttachmentBytes,
                    }),
                    response => response.Url.Contains(
                            "/org/message-threads/messages/attached-files",
                            StringComparison.Ordinal)
                        && response.Request.Method == "POST",
                    new PageRunAndWaitForResponseOptions { Timeout = 30_000 })
                .ConfigureAwait(false);
            if (!uploadResponse.Ok)
            {
                throw new SchoolManagerClientException(
                    "attachment_upload_failed",
                    "School Manager rejected the PDF attachment.");
            }

            var textareas = page.Locator("ion-textarea[placeholder='メッセージを入力'] textarea");
            var textareaCount = await textareas.CountAsync().ConfigureAwait(false);
            if (textareaCount != 1)
            {
                throw new SchoolManagerClientException(
                    "message_body_missing",
                    "The School Manager message body field was not found.");
            }

            await textareas.FillAsync(request.Body).ConfigureAwait(false);
            // The normal send control is icon-only; the labelled alternative also resolves the thread.
            var send = await GetSendControlAsync(page).ConfigureAwait(false);
            finalRequestStarted = true;
            var sendResponse = await page.RunAndWaitForResponseAsync(
                    () => send.ClickAsync(new LocatorClickOptions { Timeout = 15_000 }),
                    response => response.Url.TrimEnd('/')
                            .EndsWith("/org/message-threads", StringComparison.Ordinal)
                        && response.Request.Method == "POST",
                    new PageRunAndWaitForResponseOptions { Timeout = 30_000 })
                .ConfigureAwait(false);
            if (!sendResponse.Ok)
            {
                throw new SchoolManagerClientException(
                    "message_send_rejected",
                    "School Manager rejected the guardian message.",
                    outcomeUnknown: false);
            }

            var responseJson = await sendResponse.JsonAsync<JsonElement>()
                .ConfigureAwait(false);
            var threadId = responseJson.ValueKind == JsonValueKind.Object
                && responseJson.TryGetProperty("id", out var id)
                ? id.ToString()
                : null;
            if (string.IsNullOrWhiteSpace(threadId))
            {
                throw new SchoolManagerClientException(
                    "message_send_response_invalid",
                    "School Manager did not return a message thread identifier.",
                    outcomeUnknown: true);
            }

            return new SchoolManagerDeliveryResult(false, threadId);
        }
        catch (SchoolManagerClientException)
        {
            throw;
        }
        catch (SchoolManagerRecipientException exception)
        {
            throw new SchoolManagerClientException(
                exception.Code,
                "School Manager did not provide one safe guardian recipient.",
                outcomeUnknown: false,
                exception);
        }
        catch (JsonException exception) when (finalRequestStarted)
        {
            throw new SchoolManagerClientException(
                "message_send_outcome_unknown",
                "School Manager returned an unreadable response after sending.",
                outcomeUnknown: true,
                exception);
        }
        catch (PlaywrightException exception)
        {
            throw new SchoolManagerClientException(
                finalRequestStarted
                    ? "message_send_outcome_unknown"
                    : "school_manager_browser_failed",
                finalRequestStarted
                    ? "The browser lost confirmation after starting the final send request."
                    : "The School Manager browser operation failed before sending.",
                finalRequestStarted,
                exception);
        }
    }

    internal static async Task<ILocator> GetSendControlAsync(IPage page)
    {
        var send = page.Locator("ion-button.send-message-button");
        if (await send.CountAsync().ConfigureAwait(false) != 1
            || !await send.IsEnabledAsync().ConfigureAwait(false))
        {
            throw new SchoolManagerClientException(
                "send_control_invalid",
                "The School Manager send control was unavailable or ambiguous.");
        }

        return send;
    }

    private static async Task LoginAsync(
        IPage page,
        SchoolManagerDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await page.GotoAsync(
                request.BaseUri.AbsoluteUri,
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000,
                })
            .ConfigureAwait(false);
        var password = page.Locator("input[type='password']").First;
        if (await password.IsVisibleAsync().ConfigureAwait(false))
        {
            var username = page.Locator(
                "input:not([type]), input[type='text'], input[type='email']").First;
            await username.FillAsync(request.Credentials.Username).ConfigureAwait(false);
            await password.FillAsync(request.Credentials.Password).ConfigureAwait(false);
            var login = page.GetByRole(AriaRole.Button, new() { Name = "ログイン" });
            if (await login.CountAsync().ConfigureAwait(false) != 1)
            {
                throw new SchoolManagerClientException(
                    "login_control_invalid",
                    "The School Manager login control was unavailable or ambiguous.");
            }

            await login.ClickAsync(new LocatorClickOptions { Timeout = 15_000 })
                .ConfigureAwait(false);
            await page.WaitForURLAsync(
                    url => Uri.TryCreate(url, UriKind.Absolute, out var current)
                        && !current.AbsolutePath.Contains(
                            "login",
                            StringComparison.OrdinalIgnoreCase),
                    new PageWaitForURLOptions { Timeout = 30_000 })
                .ConfigureAwait(false);
        }

        if (!string.Equals(
                new Uri(page.Url).Host,
                request.BaseUri.Host,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new SchoolManagerClientException(
                "login_origin_changed",
                "School Manager redirected to an unexpected origin.");
        }
    }

    internal static async Task SelectExactStudentAsync(
        IPage page,
        SchoolManagerDeliveryRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await page.GotoAsync(
                new Uri(request.BaseUri, "message-threads").AbsoluteUri,
                new PageGotoOptions
                {
                    WaitUntil = WaitUntilState.DOMContentLoaded,
                    Timeout = 30_000,
                })
            .ConfigureAwait(false);
        // The add action initializes the compose state. Direct navigation to the
        // recipient route renders the list but leaves selecting a student inert.
        var add = page.Locator("ion-fab-button:visible");
        await add.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 })
            .ConfigureAwait(false);
        if (await add.CountAsync().ConfigureAwait(false) != 1)
        {
            throw new SchoolManagerClientException(
                "message_add_control_invalid",
                "The School Manager new-message control was unavailable or ambiguous.");
        }

        await add.ClickAsync().ConfigureAwait(false);
        await page.WaitForURLAsync("**/message-thread-target-users",
                new PageWaitForURLOptions { Timeout = 15_000 })
            .ConfigureAwait(false);
        var search = page.GetByRole(AriaRole.Searchbox);
        if (await search.CountAsync().ConfigureAwait(false) != 1)
        {
            throw new SchoolManagerClientException(
                "recipient_search_invalid",
                "The School Manager student search control was unavailable.");
        }

        await search.FillAsync(request.StudentNumber).ConfigureAwait(false);
        await search.PressAsync("Enter").ConfigureAwait(false);
        await page.WaitForTimeoutAsync(700).ConfigureAwait(false);
        var rows = page.Locator(".table-row");
        var count = await rows.CountAsync().ConfigureAwait(false);
        var candidates = new List<SchoolManagerStudentCandidate>(count);
        for (var index = 0; index < count; index++)
        {
            var row = rows.Nth(index);
            var identity = row.Locator(":scope > .table-data-1 > div");
            var fullName = await identity.Nth(0).InnerTextAsync().ConfigureAwait(false);
            var userCode = await row.Locator(":scope > .table-data-2")
                .InnerTextAsync()
                .ConfigureAwait(false);
            candidates.Add(new SchoolManagerStudentCandidate(
                index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Empty,
                userCode,
                fullName));
        }

        var match = SchoolManagerRecipientMatcher.Match(
            request.StudentNumber,
            request.StudentDisplayName,
            candidates);
        var rowIndex = int.Parse(
            match.UserId,
            System.Globalization.CultureInfo.InvariantCulture);
        await rows.Nth(rowIndex).Locator("ion-button").ClickAsync(
                new LocatorClickOptions { Timeout = 15_000 })
            .ConfigureAwait(false);
        await page.WaitForURLAsync(
                "**/message-thread-members-and-title",
                new PageWaitForURLOptions { Timeout = 15_000 })
            .ConfigureAwait(false);
    }

    internal static async Task VerifyGuardianOnlyAndSetTitleAsync(
        IPage page,
        string title)
    {
        var parentsAll = page.Locator(
            "ion-checkbox[formcontrolname='parentsAll']");
        var parents = page.Locator(
            "[formarrayname='parents'] ion-checkbox[formcontrolname='parent']");
        // With no student-app account, Ionic renders disabled placeholders without
        // formcontrolname. Still require both controls and verify they are unchecked.
        var studentsAll = page.Locator(
            ".captioned-checkbox:has(> .checkbox-caption:text-is('生徒')) > ion-checkbox");
        var students = page.Locator(
            "[formarrayname='students'] ion-checkbox");
        var parentCount = await parents.CountAsync().ConfigureAwait(false);
        if (await parentsAll.CountAsync().ConfigureAwait(false) != 1
            || parentCount == 0
            || await studentsAll.CountAsync().ConfigureAwait(false) != 1
            || await students.CountAsync().ConfigureAwait(false) != 1)
        {
            throw new SchoolManagerClientException(
                "guardian_controls_missing",
                "The selected student has no verifiable guardian recipients.");
        }

        var allParentsChecked = await parentsAll
            .EvaluateAsync<bool>("element => Boolean(element.checked)")
            .ConfigureAwait(false);
        var allStudentsChecked = await studentsAll
            .EvaluateAsync<bool>("element => Boolean(element.checked)")
            .ConfigureAwait(false);
        var studentChecked = await students
            .EvaluateAsync<bool>("element => Boolean(element.checked)")
            .ConfigureAwait(false);
        var allStudentsVerifiable = await IsStudentControlVerifiableAsync(studentsAll)
            .ConfigureAwait(false);
        var studentVerifiable = await IsStudentControlVerifiableAsync(students)
            .ConfigureAwait(false);
        var everyGuardianChecked = true;
        for (var index = 0; index < parentCount; index++)
        {
            everyGuardianChecked &= await parents.Nth(index)
                .EvaluateAsync<bool>("element => Boolean(element.checked)")
                .ConfigureAwait(false);
        }

        if (!allParentsChecked
            || !everyGuardianChecked
            || allStudentsChecked
            || studentChecked
            || !allStudentsVerifiable
            || !studentVerifiable)
        {
            throw new SchoolManagerClientException(
                "guardian_only_selection_invalid",
                "School Manager did not present a guardian-only recipient selection.");
        }

        var titleInput = page.Locator("ion-textarea[formcontrolname='title'] textarea");
        if (await titleInput.CountAsync().ConfigureAwait(false) != 1)
        {
            throw new SchoolManagerClientException(
                "message_title_invalid",
                "The School Manager message title field was unavailable.");
        }

        await titleInput.FillAsync(title).ConfigureAwait(false);
        var next = page.GetByRole(AriaRole.Button, new() { Name = "次へ" });
        if (await next.CountAsync().ConfigureAwait(false) != 1
            || !await next.IsEnabledAsync().ConfigureAwait(false))
        {
            throw new SchoolManagerClientException(
                "guardian_selection_not_ready",
                "The guardian-only recipient selection could not be validated.");
        }
    }

    private static Task<bool> IsStudentControlVerifiableAsync(ILocator control) =>
        control.EvaluateAsync<bool>("""
            element => {
                const name = element.getAttribute('formcontrolname');
                if (name === 'studentsAll' || name === 'student') return true;
                return name === null
                    && element.getAttribute('aria-checked') === 'false'
                    && element.classList.contains('checkbox-disabled')
                    && element.querySelector('input.aux-input[disabled]') !== null;
            }
            """);

    private static void ValidateRequest(SchoolManagerDeliveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.BaseUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(
                request.BaseUri.Host,
                "fsm.flens.jp",
                StringComparison.OrdinalIgnoreCase)
            || request.BaseUri.Port != 443)
        {
            throw new ArgumentException(
                "School Manager delivery is restricted to https://fsm.flens.jp/.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Credentials.Username)
            || string.IsNullOrEmpty(request.Credentials.Password)
            || string.IsNullOrWhiteSpace(request.StudentNumber)
            || string.IsNullOrWhiteSpace(request.StudentDisplayName)
            || string.IsNullOrWhiteSpace(request.Title)
            || string.IsNullOrWhiteSpace(request.Body)
            || string.IsNullOrWhiteSpace(request.AttachmentName)
            || request.AttachmentBytes.Length is <= 4 or > MaximumAttachmentBytes
            || request.AttachmentBytes[0] != (byte)'%'
            || request.AttachmentBytes[1] != (byte)'P'
            || request.AttachmentBytes[2] != (byte)'D'
            || request.AttachmentBytes[3] != (byte)'F')
        {
            throw new ArgumentException(
                "The School Manager delivery request is incomplete or unsafe.",
                nameof(request));
        }
    }
}
