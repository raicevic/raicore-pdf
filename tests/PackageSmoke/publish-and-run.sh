#!/usr/bin/env bash
# Publishes the package smoke test trimmed and with Native AOT for the current platform, and runs both binaries.
# Run it from the repository root after `dotnet pack --configuration Release --output artifacts`.
# Usage: tests/PackageSmoke/publish-and-run.sh [mode...]   (modes: trimmed, aot; default: both)
#
# Fails on any trim or AOT warning (ILxxxx) except the known ones below, so a dependency update that brings new
# warnings is noticed. Native AOT needs the platform's native toolchain (clang on Linux, Xcode on macOS, the Visual
# Studio C++ tools on Windows).
set -euo pipefail

# Microsoft.AspNetCore.Components reads the component's type with GetType() when it sets parameters, injects
# services and finds cascading parameters. The component types themselves are preserved: RazorToPdf and
# RazorPdfRenderer annotate the root component with [DynamicallyAccessedMembers(All)], and Razor compiles child
# components to RenderTreeBuilder.OpenComponent<T>, which is annotated too.
known='warning IL2072: Microsoft\.AspNetCore\.Components\.(CascadingParameterState\.FindCascadingParameters|ComponentFactory\.PerformPropertyInjection|Reflection\.ComponentProperties\.SetProperties)\('

if [ $# -eq 0 ]; then set -- trimmed aot; fi
work="$(pwd)/smoke-publish"
failed=0

for mode in "$@"; do
  case "$mode" in
    trimmed) property=PublishTrimmed ;;
    aot) property=PublishAot ;;
    *) echo "Unknown mode '$mode' (use trimmed or aot)." >&2; exit 2 ;;
  esac

  out="$work/$mode"
  log="$work/$mode.log"
  rm -rf "$out"
  mkdir -p "$work"
  echo "::group::Publish ($mode)"
  # Trim and AOT warnings are checked below against the known list; compiler warnings stay errors.
  dotnet publish tests/PackageSmoke --configuration Release --use-current-runtime -p:$property=true \
    -p:TrimmerSingleWarn=false -p:ILLinkTreatWarningsAsErrors=false -p:IlcTreatWarningsAsErrors=false \
    --output "$out" 2>&1 | tee "$log"
  echo "::endgroup::"

  warnings=$(grep -oE "warning (IL|AOT)[0-9]+: [^[]*" "$log" | sort -u || true)
  unexpected=$(grep -vE "$known" <<< "$warnings" || true)
  if [ -n "$unexpected" ]; then
    echo "::error::Unexpected trim/AOT warnings ($mode):"
    echo "$unexpected"
    failed=1
  fi
  echo "Known warnings ($mode): $(grep -cE "$known" <<< "$warnings" || true)"

  # HarfBuzz is a native library: it must be published next to the binary for this runtime identifier.
  if ! ls "$out"/libHarfBuzzSharp.* > /dev/null 2>&1; then
    echo "::error::libHarfBuzzSharp is missing from the $mode output."
    failed=1
  fi

  exe="$out/PackageSmoke"
  if [ -f "$exe.exe" ]; then exe="$exe.exe"; fi
  "$exe" "$work/$mode-pdfs" || failed=1
done

exit $failed
