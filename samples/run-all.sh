#!/usr/bin/env bash
# Runs every sample (built with the solution) and checks that each one wrote PDFs: CI runs it after the tests.
# Usage: samples/run-all.sh [configuration] [output-directory]
set -euo pipefail

configuration="${1:-Release}"
output="$(mkdir -p "${2:-samples-output}" && cd "${2:-samples-output}" && pwd)"
cd "$(dirname "$0")/.."

check_pdf() {
  if ! head -c 5 "$1" | grep -q '%PDF-'; then
    echo "::error::$1 is not a PDF"
    exit 1
  fi
  echo "ok $1"
}

for sample in Invoice Report Letter Worker; do
  dotnet run --project "samples/$sample" --no-build --configuration "$configuration" -- "$output"
done

for pdf in invoice-2026-0042 report letter receipt-1 receipt-2 receipt-3; do
  check_pdf "$output/$pdf.pdf"
done

# The web app: start it, fetch both endpoints, stop it.
port=5123
dotnet run --project samples/WebApp --no-build --configuration "$configuration" --urls "http://127.0.0.1:$port" > "$output/webapp.log" 2>&1 &
app=$!
trap 'kill "$app" 2>/dev/null || true' EXIT
for _ in $(seq 1 120); do
  curl -fs -o /dev/null "http://127.0.0.1:$port/" && break
  sleep 0.5
done

curl -fsS -o "$output/web-invoice.pdf" "http://127.0.0.1:$port/invoices/42.pdf"
curl -fsS -o "$output/web-statement.pdf" "http://127.0.0.1:$port/statement.pdf"
check_pdf "$output/web-invoice.pdf"
check_pdf "$output/web-statement.pdf"
