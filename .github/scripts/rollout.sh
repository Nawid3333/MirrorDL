#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Nawid Salehie
#
# Keeps one issue per release, "Release vX.Y.Z", that follows it everywhere it goes: the GitHub
# release, then the Microsoft Store. The workflows tick its steps as they happen; a failure gets the
# label release-failed and a comment that says what went wrong, with a link to the run; once every step
# is ticked, the issue closes itself. It is assigned to the repository's owner, so GitHub tells them.
#
#   rollout.sh start <tag> <steps>     open the issue (or find it); steps: github, store-submitted, store-live
#   rollout.sh tick <tag> <step> <note>  tick a step; closes the issue when it was the last one
#   rollout.sh fail <tag> <text>       report a failure: comment, label release-failed, reopen
#   rollout.sh problem <title> <text>  the same for a problem that belongs to no release (no tag yet)
#   rollout.sh waiting                 the tag of the newest open release issue the Store still has to finish
#
# Needs GH_TOKEN (with issues: write) and GH_REPO; GITHUB_SERVER_URL and GITHUB_RUN_ID for the run link.
set -euo pipefail

run_link() {
  if [ -n "${GITHUB_RUN_ID:-}" ]; then
    echo "[the run]($GITHUB_SERVER_URL/$GH_REPO/actions/runs/$GITHUB_RUN_ID)"
  fi
}

labels() {
  gh label create release --color 0e8a16 --description 'Follows a release to GitHub and the Store' --force >/dev/null
  gh label create release-failed --color d73a4a --description 'A release step failed' --force >/dev/null
}

# The number of the issue with this label and exactly this title, open or closed; empty if there is none.
# Listed, not searched: GitHub's search lags behind, and would miss an issue opened a moment ago.
find_issue() {
  gh issue list --state all --label "$2" --limit 100 --json number,title \
    --jq ".[] | select(.title == \"$1\") | .number" | head -1
}

line() {
  case $1 in
    github) echo 'GitHub release published, with both downloads checked' ;;
    store-submitted) echo 'Submitted to the Microsoft Store' ;;
    store-live) echo 'Certified and live in the Microsoft Store' ;;
    *) echo "unknown step: $1" >&2; exit 2 ;;
  esac
}

start() {
  local tag=$1 steps=$2 title="Release $1" number body step
  labels
  number=$(find_issue "$title" release)
  if [ -n "$number" ]; then echo "$number"; return; fi
  body="This issue follows MirrorDL ${tag#v} to everywhere it is released, and closes itself once it is out everywhere. If a step fails, it says so here."$'\n'
  for step in ${steps//,/ }; do
    body+=$'\n'"- [ ] <!-- $step --> $(line "$step")"
  done
  gh issue create --title "$title" --body "$body" --label release --assignee "${GH_REPO%%/*}" | sed 's|.*/||'
}

tick() {
  local tag=$1 step=$2 note=$3 number body
  number=$(find_issue "Release $tag" release)
  if [ -z "$number" ]; then number=$(start "$tag" "$step"); fi
  body=$(gh issue view "$number" --json body --jq .body)
  body=${body//"- [ ] <!-- $step -->"/"- [x] <!-- $step -->"}
  gh issue edit "$number" --body "$body" >/dev/null
  gh issue comment "$number" --body "✅ $(line "$step"). $note" >/dev/null
  if [[ $body != *'- [ ] '* ]]; then
    gh issue edit "$number" --remove-label release-failed >/dev/null 2>&1 || true
    gh issue close "$number" --comment "MirrorDL ${tag#v} is out everywhere." >/dev/null
  fi
}

fail() {
  local tag=$1 text=$2 number
  labels
  number=$(find_issue "Release $tag" release)
  if [ -z "$number" ]; then number=$(start "$tag" github); fi
  gh issue reopen "$number" >/dev/null 2>&1 || true
  gh issue edit "$number" --add-label release-failed >/dev/null
  # A check that runs every few hours finds the same failure again: it is said once.
  if [[ $(gh issue view "$number" --json comments --jq '.comments[-1].body // ""') == "❌ $text"* ]]; then return; fi
  gh issue comment "$number" --body "❌ $text $(run_link)" >/dev/null
}

problem() {
  local title=$1 text=$2 number
  labels
  number=$(find_issue "$title" release-failed)
  if [ -z "$number" ]; then
    gh issue create --title "$title" --body "❌ $text $(run_link)" --label release-failed --assignee "${GH_REPO%%/*}" >/dev/null
  else
    gh issue reopen "$number" >/dev/null 2>&1 || true
    gh issue comment "$number" --body "❌ Again: $text $(run_link)" >/dev/null
  fi
}

waiting() {
  gh issue list --state open --label release --limit 20 --json title,body \
    --jq '[.[] | select(.body | contains("- [x] <!-- store-submitted -->")) | select(.body | contains("- [ ] <!-- store-live -->"))
          | .title | ltrimstr("Release ")] | sort_by(ltrimstr("v") | split(".") | map(tonumber)) | last // empty'
}

command=${1:-}
shift || true
case $command in
  start) start "$@" ;;
  tick) tick "$@" ;;
  fail) fail "$@" ;;
  problem) problem "$@" ;;
  waiting) waiting ;;
  *) echo "usage: rollout.sh start|tick|fail|problem|waiting ..." >&2; exit 2 ;;
esac
