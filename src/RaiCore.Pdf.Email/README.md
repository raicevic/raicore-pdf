# RaiCore.Pdf.Email

Converts email messages (`.eml`) and web archives (`.mht`, `.mhtml`) to PDF with
[RaiCore.Pdf](https://www.nuget.org/packages/RaiCore.Pdf)'s HTML engine: no browser, no mail client, no native
dependencies.

```csharp
using RaiCore.Pdf.Email;

var pdf = EmailToPdf.Convert("message.eml", new EmailConverterOptions { AppendAttachments = true });
pdf.Save("message.pdf");
```

- **What a mail client shows:** a header block (from, to, cc, date, subject; your choice of fields and labels), the
  HTML body (or the plain text one, with its line breaks, `format=flowed` paragraphs and links), and inline images in
  place, from `cid:` references or `Content-Location`.
- **Attachments:** embedded as PDF file attachments, listed in the header block, and optionally appended: images and
  text files on pages of their own, PDF files with their pages, HTML files and attached messages converted.
- **MIME done properly:** multipart/alternative, related and mixed; quoted-printable and base64; charsets from UTF-8
  and the Windows code pages to ISO-2022-JP, Shift_JIS, GB18030, Big5 and EUC-KR; encoded words (RFC 2047) and
  split, encoded file names (RFC 2231).
- **Web archives:** the page with its style sheets and images, without a header block.
- **Untrusted input:** malformed messages are read as far as they can be; nesting, parts, pages and time are bounded
  by `ResourceLimits`; local files are never loaded, and remote images only through a loader you supply.

Every `HtmlConverterOptions` setting applies through `EmailConverterOptions.Html`: page size, fonts, PDF/A, tagged
PDF and PDF/UA, encryption, diagnostics.

More: the [email guide](https://raicevic.github.io/raicore-pdf/guides/email.html).

## More

- [Documentation and all packages](https://github.com/raicevic/raicore-pdf#readme)
- [Changelog](https://github.com/raicevic/raicore-pdf/blob/main/CHANGELOG.md) · [Issues](https://github.com/raicevic/raicore-pdf/issues)
- License: [Apache-2.0](https://github.com/raicevic/raicore-pdf/blob/main/LICENSE)
