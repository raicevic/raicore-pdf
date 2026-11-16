#!/usr/bin/env bash
# Works out what a release tag releases and checks it against the repository, for .github/workflows/release.yml.
# Prints GitHub step outputs (key=value lines): train, version, suffix, prerelease and projects.
#
# Usage: tools/release-plan.sh <tag>
#   v1.2.0, v1.2.0-rc.1      the core packages, at CoreVersionPrefix (Directory.Build.props)
#   RaiCore.Pdf.Docx/v0.3.0  one package with its own version, at the VersionPrefix in its project file
#
# See "Releasing" in CONTRIBUTING.md.
set -euo pipefail

fail() {
  echo "::error::$1" >&2
  exit 1
}

# A property of a project as MSBuild evaluates it.
property() {
  dotnet msbuild "$1" -getProperty:"$2"
}

tag="${1:?usage: tools/release-plan.sh <tag>}"
cd "$(dirname "$0")/.."

case "$tag" in
  v*)
    train=core
    version="${tag#v}"
    expected="$(property src/RaiCore.Pdf/RaiCore.Pdf.csproj CoreVersionPrefix)"
    where="CoreVersionPrefix in Directory.Build.props"
    projects=""
    for project in src/*/*.csproj; do
      if [[ "$(property "$project" IsCorePackage)" == "true" ]]; then
        projects="$projects $project"
      fi
    done
    if [[ "$version" == 0.* || "$version" == 1.0.0-* ]]; then
      fail "Nothing is released before 1.0; $tag is below v1.0.0. See CONTRIBUTING.md."
    fi
    ;;
  */v*)
    train="${tag%%/v*}"
    version="${tag#*/v}"
    projects="src/$train/$train.csproj"
    [[ -f "$projects" ]] || fail "$tag names no package: $projects does not exist."
    [[ "$(property "$projects" IsCorePackage)" != "true" ]] || fail "$train is a core package; release it with a v<version> tag."
    expected="$(property "$projects" VersionPrefix)"
    where="VersionPrefix in $projects"
    if ! git tag --list 'v*' | grep -Eq '^v[1-9][0-9]*\.[0-9]+\.[0-9]+$'; then
      fail "Nothing is released before 1.0: release the core (v1.0.0) before $train."
    fi
    ;;
  *)
    fail "$tag is neither v<version> nor <PackageId>/v<version>."
    ;;
esac

if ! [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  fail "$tag does not end in v<major>.<minor>.<patch>[-prerelease]."
fi

if [[ "${version%%-*}" != "$expected" ]]; then
  fail "$tag is version ${version%%-*}, but $where is $expected: set it in a pull request first."
fi

suffix=""
prerelease=false
if [[ "$version" == *-* ]]; then
  suffix="${version#*-}"
  prerelease=true
fi

echo "train=$train"
echo "version=$version"
echo "suffix=$suffix"
echo "prerelease=$prerelease"
echo "projects=${projects# }"
