# A small app that converts HTML to PDF with RaiCore.Pdf, built and run on Alpine Linux (musl), x64 or Arm64.
#
#   dotnet pack -c Release -o docs/docker/packages    # until 1.0 is on NuGet (see nuget.config)
#   docker build -t hello-pdf docs/docker
#   docker run --rm hello-pdf > hello.pdf
#
# No fonts or fontconfig are needed in the image: RaiCore.Pdf.Fonts bundles Liberation Sans, Serif and Mono for
# Arial, Helvetica, Times New Roman, Courier New and the generic families, and other fonts come from @font-face or
# files you load. HarfBuzz ships in the packages for linux-musl-x64 and linux-musl-arm64 (and glibc Linux, Windows
# and macOS); it needs libstdc++, which every .NET image has.

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
# -a picks the runtime identifier (linux-musl-x64 or linux-musl-arm64), so only that HarfBuzz library is published.
RUN dotnet publish -a $TARGETARCH --configuration Release --output /app

# The -extra images add ICU and time zone data. The plain Alpine runtime images run in globalization-invariant mode:
# PDFs come out the same, except that culture-specific formatting in your own code uses the invariant culture and
# AES-256 passwords are not NFKC-normalized (use ASCII passwords there, or install icu-libs).
FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine-extra
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "HelloPdf.dll"]
