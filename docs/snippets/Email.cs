#pragma warning disable CA1849, CA2007
using RaiCore.Pdf.Email;
using RaiCore.Pdf.Html;

namespace Snippets;

internal static class Email
{
    public static async Task RunAsync()
    {
        // A message to convert: HTML with a plain text alternative, and a text file attached.
        await File.WriteAllTextAsync("message.eml", """
            From: Alice Example <alice@example.com>
            To: Bob Example <bob@example.org>
            Subject: =?utf-8?Q?Caf=C3=A9_menu?=
            Date: Thu, 19 Nov 2026 09:00:00 +0100
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="mixed"

            --mixed
            Content-Type: multipart/alternative; boundary="alt"

            --alt
            Content-Type: text/plain; charset=utf-8

            The new menu is attached.
            --alt
            Content-Type: text/html; charset=utf-8

            <p>The new <b>menu</b> is attached.</p>
            --alt--
            --mixed
            Content-Type: text/plain; charset=utf-8
            Content-Disposition: attachment; filename="menu.txt"

            Soup of the day
            Bread and cheese
            --mixed--
            """.ReplaceLineEndings("\r\n"));

        #region convert
        var pdf = EmailToPdf.Convert("message.eml", new EmailConverterOptions { AppendAttachments = true });
        pdf.Save("message.pdf");
        #endregion

        #region options
        var options = new EmailConverterOptions
        {
            HeaderFields = ["Subject", "From", "Date", "Reply-To"],
            HeaderLabels = new Dictionary<string, string> { ["From"] = "Fra", ["Date"] = "Dato", ["Attachments"] = "Bilag" },
            Html = new HtmlConverterOptions
            {
                Page = new(RaiCore.Pdf.PdfPageSize.Letter, RaiCore.Pdf.Layout.Thickness.All(54)),
                UserStyleSheet = ".raicore-email-subject { color: #1a5276 } .raicore-email-text { font-family: serif }",
                // Remote images load only through a loader that fetches them; this one allows one host.
                ResourceLoader = request => request.Uri.Host == "images.example.com" ? DownloadImage(request.Uri) : null,
            },
        };
        await using (var message = File.OpenRead("message.eml"))
        {
            var document = await EmailToPdf.ConvertAsync(message, options);
            document.Save("message-custom.pdf");
        }
        #endregion
    }

    private static byte[]? DownloadImage(Uri uri) => null; // A real loader would fetch it, e.g. with HttpClient.
}
