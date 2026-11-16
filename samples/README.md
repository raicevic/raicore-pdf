# Samples

Runnable apps that show RaiCore.Pdf in typical jobs. They are part of `RaiCore.Pdf.slnx`, so every CI build
compiles them, and CI runs them on Linux (`samples/run-all.sh`) and publishes their PDFs as the `samples` artifact.

They reference the library projects in this repository; in your own app, reference the packages instead (each project
file names the one it needs).

| Sample | What it shows | Run |
|---|---|---|
| [Invoice](Invoice/Program.cs) | An HTML template filled from C# data: a table whose header repeats on every page, totals kept together, a footer with page numbers, written straight to a file page by page. | `dotnet run --project samples/Invoice` |
| [Report](Report/Program.cs) | A multi-page report: a title page without header or footer (a named page), a table of contents with dot leaders and page numbers (`target-counter`), the chapter title in the running header (`string-set`), chapters on new pages, hyphenated justified text and bookmarks. | `dotnet run --project samples/Report` |
| [Letter](Letter/Program.cs) | A business letter for a window envelope (DIN 5008 form B): the address field placed where the window is, fold and punch marks in the margin. | `dotnet run --project samples/Letter` |
| [WebApp](WebApp/Program.cs) | ASP.NET Core with Razor components: `AddRazorPdf()`, a component that injects a service, a PDF returned as a file (`/invoices/42.pdf`) and a long one streamed into the response (`/statement.pdf`). | `dotnet run --project samples/WebApp`, then open the URL it prints |
| [Worker](Worker/Program.cs) | A background service converting queued jobs, with resource limits and the converter's diagnostics in the app's log. The same code fits an Azure Function or another job runner. | `dotnet run --project samples/Worker` |
| [Docker](Docker/Dockerfile) | A small HTML-to-PDF app in an Alpine image (x64 or Arm64), built from the packages: what an image needs and does not need. | see the [Dockerfile](Docker/Dockerfile) |

The console samples write to `output/` in the current directory, or to the directory given as their argument.
