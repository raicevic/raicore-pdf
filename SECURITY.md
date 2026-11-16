# Security policy

## Reporting a vulnerability

Please report vulnerabilities privately, through GitHub's
[private vulnerability reporting](https://github.com/raicevic/raicore-pdf/security/advisories/new) (the *Report a
vulnerability* button on the [Security tab](https://github.com/raicevic/raicore-pdf/security)). Do not open a public
issue, pull request or discussion for it.

A useful report has:

- the package and version (or commit) affected;
- the input that triggers it (an HTML document, a PDF, a font or an image), minimized if you can;
- what happens (a crash, a hang, memory or disk use, a file read or a request the document should not cause) and what
  you expected;
- the options used, in particular `ResourceLimits`, `ResourceLoader` and `BaseUri`.

You should get an answer within a week. We will work out a fix with you, publish it in a new release and then publish
a GitHub security advisory that credits you, unless you prefer not to be named.

## Supported versions

Nothing has been released yet; the first release will be 1.0.0. After that, security fixes go into the latest minor
release (see [SUPPORT.md](SUPPORT.md)).

## Scope

RaiCore.Pdf is meant to convert untrusted HTML and to read untrusted PDF, font and image files. In scope, for
example:

- a crash, hang or exception other than the ones the API documents (such as `PdfException` and its subclasses,
  `ResourceLimitExceededException` and `OperationCanceledException`) on any input;
- work, memory or output size that `ResourceLimits` should bound but does not;
- a document that makes the default `ResourceLoader` read something other than `data:` URIs and `file:` URIs, or makes
  the converter load anything without going through the `ResourceLoader`;
- weaknesses in PDF encryption as written or read (the standard security handler).

Out of scope: which URIs a `ResourceLoader` loads, and limits you raise yourself. The default loader reads `file:`
URIs, so a document can reference local files; for untrusted HTML, supply a loader that allows only what the
document should reach.
