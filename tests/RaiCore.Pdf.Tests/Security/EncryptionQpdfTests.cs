using System.Diagnostics;
using System.Text;

namespace RaiCore.Pdf.Tests.Security;

/// <summary>qpdf decrypts and checks the encrypted output. Skipped when qpdf is not installed; CI installs it.</summary>
public class EncryptionQpdfTests
{
    private const string UserPassword = "user secret";
    private const string OwnerPassword = "owner secret";

    private static byte[] Encrypted(PdfEncryptionAlgorithm algorithm = PdfEncryptionAlgorithm.Aes256, string? userPassword = UserPassword, PdfPermissions permissions = PdfPermissions.Print)
    {
        var doc = EncryptionSamples.CreateRichSample(new PdfEncryption
        {
            UserPassword = userPassword,
            OwnerPassword = OwnerPassword,
            Permissions = permissions,
            Algorithm = algorithm,
        });
        doc.CompressContent = false;
        return doc.ToArray();
    }

    private static byte[] Plain() => EncryptionSamples.CreateRichSample(null).ToArray();

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256, "R = 6", "AESv3")]
    [InlineData(PdfEncryptionAlgorithm.Aes128, "R = 4", "AESv2")]
    public void Qpdf_shows_the_encryption_and_permissions(PdfEncryptionAlgorithm algorithm, string revision, string method)
    {
        var (exitCode, output) = ExternalTools.RunOnPdf("qpdf", Encrypted(algorithm), $"--password={OwnerPassword}", "--show-encryption", "{pdf}");

        Assert.True(exitCode == 0, output);
        Assert.Contains(revision, output, StringComparison.Ordinal);
        Assert.Contains(algorithm == PdfEncryptionAlgorithm.Aes256 ? "P = -3388" : "P = -3900", output, StringComparison.Ordinal);
        Assert.Contains("Supplied password is owner password", output, StringComparison.Ordinal);
        Assert.Contains("print low resolution: allowed", output, StringComparison.Ordinal);
        Assert.Contains("print high resolution: not allowed", output, StringComparison.Ordinal);
        Assert.Contains("extract for any purpose: not allowed", output, StringComparison.Ordinal);
        Assert.Contains("modify other: not allowed", output, StringComparison.Ordinal);
        Assert.Contains($"stream encryption method: {method}", output, StringComparison.Ordinal);
        Assert.Contains($"string encryption method: {method}", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256, UserPassword)]
    [InlineData(PdfEncryptionAlgorithm.Aes256, OwnerPassword)]
    [InlineData(PdfEncryptionAlgorithm.Aes128, UserPassword)]
    public void Qpdf_checks_the_encrypted_file_with_a_password(PdfEncryptionAlgorithm algorithm, string password)
    {
        var (exitCode, output) = ExternalTools.RunOnPdf("qpdf", Encrypted(algorithm), $"--password={password}", "--check", "{pdf}");

        Assert.True(exitCode == 0, output);
        Assert.Contains("No syntax or stream encoding errors found", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Qpdf_rejects_a_wrong_password()
    {
        var (exitCode, output) = ExternalTools.RunOnPdf("qpdf", Encrypted(), "--password=wrong", "--check", "{pdf}");

        Assert.NotEqual(0, exitCode);
        Assert.Contains("password", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Qpdf_says_only_the_user_password_file_requires_a_password()
    {
        // --requires-password exits 0 when a password is needed, 3 when the file is encrypted but opens without one, and
        // 2 when it is not encrypted.
        Assert.Equal(0, ExternalTools.RunOnPdf("qpdf", Encrypted(), "--requires-password", "{pdf}").ExitCode);
        Assert.Equal(3, ExternalTools.RunOnPdf("qpdf", Encrypted(userPassword: null), "--requires-password", "{pdf}").ExitCode);
        Assert.Equal(2, ExternalTools.RunOnPdf("qpdf", Plain(), "--requires-password", "{pdf}").ExitCode);
    }

    [Theory]
    [InlineData(PdfEncryptionAlgorithm.Aes256, UserPassword)]
    [InlineData(PdfEncryptionAlgorithm.Aes256, OwnerPassword)]
    [InlineData(PdfEncryptionAlgorithm.Aes128, OwnerPassword)]
    public void Qpdf_decrypts_to_a_clean_file_with_the_plain_content(PdfEncryptionAlgorithm algorithm, string password)
    {
        var decrypted = Decrypt(Encrypted(algorithm), password);
        var text = Encoding.Latin1.GetString(decrypted);

        Assert.DoesNotContain("/Encrypt", text, StringComparison.Ordinal);
        Assert.Contains("(Hello from RaiCore.Pdf) Tj", text, StringComparison.Ordinal);
        Assert.Contains("Secret Author", text, StringComparison.Ordinal);
        Assert.Contains("https://example.com/secret-link", text, StringComparison.Ordinal);
        ExternalTools.AssertQpdfClean(decrypted);
    }

    [Fact]
    public void Qpdf_decrypts_an_owner_only_file_without_a_password()
    {
        var decrypted = Decrypt(Encrypted(userPassword: null), password: null);

        Assert.Contains("(Hello from RaiCore.Pdf) Tj", Encoding.Latin1.GetString(decrypted), StringComparison.Ordinal);
    }

    /// <summary>Runs <c>qpdf --decrypt</c> (keeping streams as they are) and returns the output file.</summary>
    private static byte[] Decrypt(byte[] pdf, string? password)
    {
        var qpdf = ExternalTools.Find("qpdf");
        Assert.SkipWhen(qpdf is null, "qpdf is not installed (it runs in CI).");

        var input = Path.Combine(Path.GetTempPath(), $"raicore-{Guid.NewGuid():N}.pdf");
        var output = Path.Combine(Path.GetTempPath(), $"raicore-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(input, pdf);
            var start = new ProcessStartInfo(qpdf!) { RedirectStandardOutput = true, RedirectStandardError = true };
            if (password is not null)
            {
                start.ArgumentList.Add($"--password={password}");
            }

            foreach (var argument in new[] { "--decrypt", "--object-streams=disable", "--stream-data=preserve", input, output })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, stdout.Result + stderr.Result);
            return File.ReadAllBytes(output);
        }
        finally
        {
            File.Delete(input);
            File.Delete(output);
        }
    }
}
