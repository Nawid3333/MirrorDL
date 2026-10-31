#!/usr/bin/env bash
# SPDX-License-Identifier: GPL-3.0-or-later
# Copyright (C) 2026 Nawid Salehie
#
# Shows on each release's page where it is - GitHub, then the Microsoft Store - and opens an issue only
# when a step fails. The release notes get a short list that the workflows keep up to date:
#
#   ✅ GitHub: published, both downloads checked - 2026-11-01
#   ✅ Microsoft Store: submitted - 2026-11-01
#   ⏳ Microsoft Store: in certification (hours to a few days)
#
# A failure turns its line into ❌ and opens the issue "Release vX.Y.Z: a step failed", assigned to
# the repository's owner (so GitHub tells them), or comments on it; the same failure is said once. Once
# no line is ❌ anymore - after a re-run, say - the issue closes itself.
#
#   rollout.sh start <tag> <steps>         add the list, every step waiting; steps: github,store-submitted,store-live
#   rollout.sh ok <tag> <step>             tick a step
#   rollout.sh fail <tag> <step> <text>    a step failed: why, in a sentence or two
#   rollout.sh problem <title> <text>      a failure that belongs to no release (there is no version yet)
#   rollout.sh waiting                     the newest release the Store still has to finish, if any
#
# Needs GH_TOKEN (contents: write for the release notes, issues: write) and GH_REPO; GITHUB_SERVER_URL
# and GITHUB_RUN_ID for the link to the run.
set -euo pipefail

today=$(date -u +%Y-%m-%d)

run_link() {
  if [ -n "${GITHUB_RUN_ID:-}" ]; then
    echo "[the run]($GITHUB_SERVER_URL/$GH_REPO/actions/runs/$GITHUB_RUN_ID)"
  fi
}

# What a step's line says, by state: wait, ok or fail.
text() {
  case $1:$2 in
    github:wait) echo 'GitHub: being built' ;;
    github:ok) echo "GitHub: published, both downloads checked - $today" ;;
    github:fail) echo 'GitHub: something failed (see the issue)' ;;
    store-submitted:wait) echo 'Microsoft Store: not submitted yet' ;;
    store-submitted:ok) echo "Microsoft Store: submitted - $today" ;;
    store-submitted:fail) echo 'Microsoft Store: submitting failed (see the issue)' ;;
    store-live:wait) echo 'Microsoft Store: in certification (hours to a few days)' ;;
    store-live:ok) echo "Microsoft Store: certified and live - $today" ;;
    store-live:fail) echo 'Microsoft Store: a problem (see the issue)' ;;
    *) echo "unknown step or state: $1 $2" >&2; exit 2 ;;
  esac
}

icon() {
  case $1 in wait) echo '⏳' ;; ok) echo '✅' ;; fail) echo '❌' ;; esac
}

notes() { gh release view "$1" --json body --jq .body; }

# Sets one step's line in the release notes; adds the list if it isn't there yet.
set_line() {
  local tag=$1 step=$2 state=$3 body new
  body=$(notes "$tag")
  new="- $(icon "$state") $(text "$step" "$state") <!-- $step -->"
  if [[ $body == *"<!-- $step -->"* ]]; then
    body=$(awk -v step="<!-- $step -->" -v line="$new" 'index($0, step) { print line; next } { print }' <<< "$body")
  else
    if [[ $body != *'<!-- status -->'* ]]; then body+=$'\n\n<!-- status -->\n**Where this release is**'; fi
    body+=$'\n'"$new"
  fi
  gh release edit "$tag" --notes "$body" >/dev/null
}

labels() {
  gh label create release-failed --color d73a4a --description 'A release step failed' --force >/dev/null
}

# The open or closed issue with exactly this title and the label release-failed; empty if there is none.
# Listed, not searched: GitHub's search lags behind, and would miss an issue opened a moment ago.
find_issue() {
  gh issue list --state all --label release-failed --limit 100 --json number,title \
    --jq ".[] | select(.title == \"$1\") | .number" | head -1
}

# Opens the issue, or comments on it - unless its last comment already says the same.
report() {
  local title=$1 text=$2 number
  labels
  number=$(find_issue "$title")
  if [ -z "$number" ]; then
    gh issue create --title "$title" --body "❌ $text $(run_link)" --label release-failed --assignee "${GH_REPO%%/*}" >/dev/null
    return
  fi
  gh issue reopen "$number" >/dev/null 2>&1 || true
  if [[ $(gh issue view "$number" --json comments,body --jq '(.comments[-1].body // .body)') == "❌ $text"* ]]; then return; fi
  gh issue comment "$number" --body "❌ $text $(run_link)" >/dev/null
}

start() {
  local tag=$1 step
  for step in ${2//,/ }; do
    if [[ $(notes "$tag") != *"<!-- $step -->"* ]]; then set_line "$tag" "$step" wait; fi
  done
}

ok() {
  local tag=$1 step=$2 number
  set_line "$tag" "$step" ok
  if [ "$step" = store-submitted ] && [[ $(notes "$tag") == *'<!-- store-live -->'* ]]; then set_line "$tag" store-live wait; fi
  # Nothing failed anymore: the issue, if there is one, has served its purpose.
  number=$(find_issue "Release $tag: a step failed")
  if [ -n "$number" ] && [[ $(notes "$tag") != *'❌'* ]] && [ "$(gh issue view "$number" --json state --jq .state)" = OPEN ]; then
    gh issue close "$number" --comment "✅ Resolved: $(text "$step" ok). $(run_link)" >/dev/null
  fi
}

fail() {
  set_line "$1" "$2" fail
  report "Release $1: a step failed" "$3"
}

# The newest of the last releases whose Store submission went through but which isn't live yet.
waiting() {
  local tag body
  for tag in $(gh release list --limit 5 --exclude-drafts --json tagName --jq '.[].tagName'); do
    body=$(notes "$tag")
    if [[ $body == *'✅ Microsoft Store: submitted'* && $body == *'<!-- store-live -->'* && $body != *'✅ Microsoft Store: certified and live'* ]]; then
      echo "$tag"
      return
    fi
  done
}

command=${1:-}
shift || true
case $command in
  start) start "$@" ;;
  ok) ok "$@" ;;
  fail) fail "$@" ;;
  problem) report "$@" ;;
  waiting) waiting ;;
  *) echo "usage: rollout.sh start|ok|fail|problem|waiting ..." >&2; exit 2 ;;
esac
