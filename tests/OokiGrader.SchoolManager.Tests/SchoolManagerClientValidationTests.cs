using OokiGrader.SchoolManager.Delivery;

namespace OokiGrader.SchoolManager.Tests;

public sealed class SchoolManagerClientValidationTests
{
    [Fact]
    public async Task RejectsUnexpectedOriginBeforeLaunchingBrowser()
    {
        var client = new PlaywrightSchoolManagerClient();
        var request = ValidRequest() with
        {
            BaseUri = new Uri("https://example.test/"),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => client.DeliverAsync(request));
    }

    [Fact]
    public async Task RejectsNonPdfAttachmentBeforeLaunchingBrowser()
    {
        var client = new PlaywrightSchoolManagerClient();
        var request = ValidRequest() with
        {
            AttachmentBytes = "not a pdf"u8.ToArray(),
        };

        await Assert.ThrowsAsync<ArgumentException>(() => client.DeliverAsync(request));
    }

    private static SchoolManagerDeliveryRequest ValidRequest() => new(
        new Uri("https://fsm.flens.jp/"),
        new SchoolManagerCredentials("automation", "secret"),
        "10000001",
        "試験 太郎",
        "採点結果のお知らせ",
        "添付をご確認ください。",
        "result.pdf",
        "%PDF-1.7"u8.ToArray(),
        DryRun: true);
}
