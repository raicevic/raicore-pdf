using System.Text;
using PDFtoImage;
using PDFtoImage.Exceptions;
using RaiCore.Pdf.Html;
using RaiCore.Pdf.Razor;
using RaiCore.Pdf.Tests.Razor.Components;
using RaiCore.Pdf.Tests.Security;

namespace RaiCore.Pdf.Tests.Html;

/// <summary><see cref="HtmlConverterOptions.Encryption"/>, which Razor conversion shares.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
public class HtmlEncryptionTests
{
    private const string Html = "<html><head><title>Quarterly secrets</title></head><body><h1>Results</h1><p>Revenue grew.</p></body></html>";

    [Fact]
    public void Html_options_encrypt_the_document()
    {
        var encryption = new PdfEncryption { UserPassword = "open", OwnerPassword = "admin", Permissions = PdfPermissions.Print };

        var document = HtmlToPdf.Convert(Html, new HtmlConverterOptions { Encryption = encryption });
        var bytes = document.ToArray();

        Assert.Same(encryption, document.Encryption);
        Assert.DoesNotContain("Quarterly secrets", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("RaiCore.Pdf.Html", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);

        var key = TestPdfDecryptor.DeriveR6Key(bytes, "open");
        Assert.NotNull(key);
        var info = TestPdfDecryptor.DecryptObjects(bytes, (data, _) => TestPdfDecryptor.AesDecrypt(key, data));
        var strings = info.Values.SelectMany(t => t).Where(t => t.Kind == TokenKind.String).Select(t => Encoding.Latin1.GetString(t.Bytes));
        Assert.Contains("Quarterly secrets", strings);
        Assert.Contains("RaiCore.Pdf.Html", strings);

        Assert.Throws<PdfPasswordProtectedException>(() => Conversion.GetPageCount(bytes));
        Assert.Equal(1, Conversion.GetPageCount(bytes, password: "open"));
    }

    [Fact]
    public void Html_output_is_unencrypted_by_default()
    {
        var bytes = HtmlToPdf.ConvertToBytes(Html);

        Assert.DoesNotContain("/Encrypt", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.Contains("Quarterly secrets", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Razor_conversion_takes_the_same_option()
    {
        var bytes = RazorToPdf.ConvertToBytes<Greeting>(
            new Dictionary<string, object?> { [nameof(Greeting.Name)] = "Ada" },
            new HtmlConverterOptions { Encryption = new PdfEncryption { OwnerPassword = "admin", Permissions = PdfPermissions.None } });

        Assert.DoesNotContain("RaiCore.Pdf.Razor", Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.NotNull(TestPdfDecryptor.DeriveR6Key(bytes, string.Empty));
        Assert.Equal(1, Conversion.GetPageCount(bytes));
    }
}
