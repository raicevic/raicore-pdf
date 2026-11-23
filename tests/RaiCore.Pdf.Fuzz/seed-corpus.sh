#!/usr/bin/env bash
# Copies the seed corpus of a fuzz target into a folder: the test fixtures of its format, the hand-made seeds in
# Fixtures/Fuzz/Seeds/<target> and the inputs that once failed, in Fixtures/Fuzz/Crashes/<target>.
# Usage: seed-corpus.sh <target> <folder>
set -euo pipefail
target="$1"
out="$2"
fixtures="$(cd "$(dirname "$0")/../RaiCore.Pdf.Tests/Fixtures" && pwd)"
mkdir -p "$out"
shopt -s nullglob
case "$target" in
  png) files=("$fixtures"/PngSuite/*.png) ;;
  jpeg) files=("$fixtures"/Images/*.jpg) ;;
  gif) files=("$fixtures"/Images/*.gif) ;;
  bmp) files=("$fixtures"/Bmp/*.bmp) ;;
  webp) files=("$fixtures"/WebP/*.webp) ;;
  tiff) files=("$fixtures"/Images/Tiff/*.tif) ;;
  svg) files=("$fixtures"/Svg/*.svg) ;;
  font) files=("$fixtures"/*.ttf "$fixtures"/*.otf "$fixtures"/*.ttc) ;;
  pdf) files=("$fixtures"/Reader/*.pdf "$fixtures"/Reader/Encrypted/*.pdf) ;;
  extract) files=("$fixtures"/Reader/*.pdf "$fixtures"/Extraction/*.pdf) ;;
  edit) files=("$fixtures"/Reader/*.pdf "$fixtures"/Forms/*.pdf "$fixtures"/Annotations/*.pdf "$fixtures"/Extraction/*.pdf) ;;
  cfb) files=("$fixtures"/Cfb/*.cfb "$fixtures"/Poi/*.msg "$fixtures"/Poi/*.doc "$fixtures"/Poi/*.xls "$fixtures"/Poi/*.ppt) ;;
  email) files=("$fixtures"/Email/* "$fixtures"/Msg/*.msg "$fixtures"/Poi/*.msg) ;;
  ppt) while IFS= read -r file; do files+=("$file"); done < <(find "$fixtures"/Slides/Ppt "$fixtures"/Poi -name '*.ppt' -size -200k | sort) ;;
  xls) while IFS= read -r file; do files+=("$file"); done < <(find "$fixtures"/Sheets/Xls -name '*.xls' -size -40k | sort) ;;
  rtf) while IFS= read -r file; do files+=("$file"); done < <(find "$fixtures"/Tika -name '*.rtf' -size -40k | sort) ;;
  doc) while IFS= read -r file; do files+=("$file"); done < <(find "$fixtures"/Documents/Doc -name '*.doc' -size -40k | sort) ;;
  doctext) files=("$fixtures"/Fuzz/Seeds/docx/* "$fixtures"/Fuzz/Seeds/odt/* "$fixtures"/Fuzz/Seeds/rtf/*)
    while IFS= read -r file; do files+=("$file"); done < <(find "$fixtures"/Documents/Doc -name '*.doc' -size -40k | sort) ;;
  *) files=() ;;
esac
files+=("$fixtures"/Fuzz/Seeds/"$target"/* "$fixtures"/Fuzz/Crashes/"$target"/*)
if [ ${#files[@]} -eq 0 ]; then
  echo "No seeds for $target" >&2
  exit 1
fi
cp "${files[@]}" "$out"/
echo "$target: ${#files[@]} seeds"
