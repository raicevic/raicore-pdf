# Email and web archives

`RaiCore.Pdf.Email` converts email messages (`.eml`, RFC 5322 and MIME) and web archives (`.mht`, `.mhtml`) to PDF
through the HTML engine.

```sh
dotnet add package RaiCore.Pdf.Email
```

[!code-csharp[](../snippets/Email.cs#convert)]

`EmailToPdf.Convert` takes a path, a stream or bytes; `ConvertAsync` and `ConvertToBytes` do the same. The PDF has:

- **A header block:** the subject as a heading, then from, to, cc and date, and the list of attachments with their
  sizes. Address fields show each name and address, decoded.
- **The body a mail client shows:** the HTML version of the message (or the plain text one), with its style sheets and
  its inline images in place.
- **The attachments,** embedded as PDF file attachments that the reader can open.

## The body

| Message structure | What is shown |
|---|---|
| `multipart/alternative` | The last HTML version, else the last plain text one. A calendar invitation (`text/calendar`) becomes an attachment. |
| `multipart/related` | The root part, with the images and style sheets it refers to by `cid:` or by `Content-Location`. |
| `multipart/mixed` | Each text and HTML part in turn, and images marked `inline` where they are (as Apple Mail sends them). |

Plain text keeps its line breaks and wraps long lines; `format=flowed` paragraphs (RFC 3676) are joined so they wrap
to the page, and web and mail addresses become links. Quoted-printable and base64 are decoded, and so are charsets from
UTF-8 and the ISO-8859 and Windows code pages to ISO-2022-JP, Shift_JIS, EUC-JP, GBK, GB18030, Big5 and EUC-KR. Header
fields and file names may be encoded words (RFC 2047) or split, encoded parameters (RFC 2231).

Text in scripts the bundled Liberation fonts lack (Japanese, Chinese, Korean) needs a font that has them: add one to
`Html.Fonts` and name it in `Html.FallbackFontFamilies`.

A message's `@page` rules and named pages (Outlook's HTML sets a Letter page with one-inch margins) are ignored, so the
page is always `Html.Page`.

## Options

[!code-csharp[](../snippets/Email.cs#options)]

| Option | What it does |
|---|---|
| `Html` | Every `HtmlConverterOptions` setting: page, fonts, limits, PDF/A, tagging, encryption, diagnostics. Its `UserStyleSheet` comes after the header block's built-in styles. |
| `HeaderFields` | The fields shown, in order (any field, such as `Reply-To`); `Subject` is the heading. `[]` shows none. |
| `HeaderLabels` | Labels by field name, and `Attachments` for the list: `["From"] = "Fra"`. |
| `EmbedAttachments` | Embed the attachments in the PDF (default `true`). PDF/A-2 does not allow it; use PDF/A-3. |
| `ListAttachments` | List the attachments in the header block (default `true`). |
| `AppendAttachments` | Append what can be shown after the message (default `false`), see below. |

The header block's elements have classes for styling: `raicore-email-header`, `raicore-email-subject`,
`raicore-email-text` (plain text bodies) and `raicore-email-attachment` (appended attachments).

The PDF's title is the subject and its author the sender, unless `Html.Title` and `Html.Author` say otherwise; its
language comes from the HTML's `lang` or the message's `Content-Language`.

## Appending attachments

With `AppendAttachments`, each attachment that can be shown starts a new page after the message:

- **Images** (JPEG, PNG, GIF, BMP, WebP, SVG) and **plain text files**, with their name as a heading that the
  attachment list links to.
- Then **PDF files** (their pages, at their own size), **HTML files** and **attached messages** (`message/rfc822`,
  converted like the message itself), each with a bookmark.

Other files are only listed and embedded. A damaged or encrypted PDF is left out (still embedded) and reported through
`Html.Diagnostics`, and so is anything that would take the document past `ResourceLimits.MaxPages`.

## Web archives

A web archive (MHTML, as browsers and Word save pages) is a `multipart/related` message whose parts have
`Content-Location` URLs: the page's style sheets and images are found by them, relative URLs included. A message
without recipients that is a `multipart/related` page or says it was saved by a browser is taken for a web archive and
gets no header block, unless `HeaderFields` names fields.

## Untrusted messages

- **Malformed messages** are read as far as they can be, the way mail clients show them: a missing closing boundary, a
  broken encoding or an unknown charset does not fail the conversion.
- **Limits:** `Html.Limits` bounds the nesting of parts (`MaxNestingDepth`), their number (`MaxElements`), the pages
  (attachments included) and the time (`Timeout`, for the whole conversion).
- **No local files:** images and style sheets come from the message itself. Other URLs go to `Html.ResourceLoader`,
  except `file:` URLs, which a message never loads; the default loader loads nothing from the network, so remote
  images stay out unless you supply a loader that fetches them.

Not supported: Outlook's `.msg` files and TNEF (`winmail.dat`) attachments (embedded as they are), S/MIME and PGP
encrypted messages (their parts are attachments), and uuencoded content.
