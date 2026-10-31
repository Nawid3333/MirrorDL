#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Nawid Salehie
#
# Checks a published release the way a user gets it: it is public, and both zips download, are whole,
# and hold their program. Right after publishing, a download can still fail for a moment, so each
# one is tried a few times.
#
#   check-release.sh <tag>
#
# Needs GH_TOKEN and GH_REPO.
set -euo pipefail
tag=$1
dir=$(mktemp -d)
trap 'rm -rf "$dir"' EXIT

if ! draft=$(gh release view "$tag" --json isDraft --jq .isDraft); then
  echo "::error::There is no release $tag."
  exit 1
fi
if [ "$draft" != false ]; then
  echo "::error::$tag is still a draft."
  exit 1
fi

for attempt in 1 2 3 4 5; do
  rm -rf "${dir:?}"/*
  if gh release download "$tag" --dir "$dir" --pattern '*.zip'; then break; fi
  if [ "$attempt" = 5 ]; then
    echo "::error::Could not download the files of $tag."
    exit 1
  fi
  echo "Download failed; trying again in $((attempt * 15)) s."
  sleep $((attempt * 15))
done

for pair in MirrorDL-win-x64.zip:MirrorDL.exe mirrordl-terminal-win-x64.zip:mirrordl.exe; do
  zip=${pair%%:*} exe=${pair#*:}
  if [ ! -f "$dir/$zip" ]; then
    echo "::error::$tag has no $zip."
    exit 1
  fi
  if ! unzip -tq "$dir/$zip" >/dev/null; then
    echo "::error::$zip of $tag is damaged."
    exit 1
  fi
  if ! unzip -Z1 "$dir/$zip" | grep -qx "$exe"; then
    echo "::error::$zip of $tag has no $exe."
    exit 1
  fi
  echo "$zip: $(stat -c %s "$dir/$zip") bytes, whole, with $exe"
done
