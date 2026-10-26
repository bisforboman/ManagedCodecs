#!/usr/bin/env bash
# For upstream.yml: files an issue titled $1 with the body on stdin, unless one with that title exists (open or
# closed). With DRY_RUN=1 (the workflow's runs on pull requests) it only says what it would file.
set -euo pipefail
title=$1
body=$(cat)
if [ "${DRY_RUN:-0}" = 1 ]; then
  { echo "Would file: $title"; echo; echo "$body"; } | tee -a "${GITHUB_STEP_SUMMARY:-/dev/null}"
  exit 0
fi
if gh issue list --state all --search "\"$title\" in:title" --json title --jq '.[].title' | grep -qxF "$title"; then
  echo "Already filed: $title"
  exit 0
fi
gh issue create --title "$title" --body "$body"
