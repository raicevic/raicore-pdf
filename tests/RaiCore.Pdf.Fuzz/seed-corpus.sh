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
  svg) files=("$fixtures"/Svg/*.svg) ;;
  font) files=("$fixtures"/*.ttf "$fixtures"/*.otf "$fixtures"/*.ttc) ;;
  pdf) files=("$fixtures"/Reader/*.pdf) ;;
  *) files=() ;;
esac
files+=("$fixtures"/Fuzz/Seeds/"$target"/* "$fixtures"/Fuzz/Crashes/"$target"/*)
if [ ${#files[@]} -eq 0 ]; then
  echo "No seeds for $target" >&2
  exit 1
fi
cp "${files[@]}" "$out"/
echo "$target: ${#files[@]} seeds"
