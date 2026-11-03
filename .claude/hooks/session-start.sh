#!/bin/bash
# Cloud sessions only: the .NET 10 SDK, FFmpeg and ImageMagick (what tools/make-samples.sh needs; Ubuntu 24.04's FFmpeg
# is 6.1, CI's version), the local tools (bro), the restored solution and the pre-commit hook. Idempotent: the container
# is cached after it runs, so a later start finds everything in place and only checks. It never fails the session start:
# a step that fails is logged and the rest go on.
set -uo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "$CLAUDE_PROJECT_DIR" || exit 0
step() { "$@" || echo "session-start: '$*' failed; carrying on."; }

# Ubuntu's own packages: the cloud network policy blocks builds.dotnet.microsoft.com (dotnet-install.sh) but not the
# Ubuntu archive.
missing=()
dotnet --list-sdks 2>/dev/null | grep -q '^10\.' || missing+=(dotnet-sdk-10.0)
command -v ffmpeg > /dev/null || missing+=(ffmpeg)
command -v magick > /dev/null || command -v convert > /dev/null || missing+=(imagemagick)
if [ ${#missing[@]} -gt 0 ]; then
  step apt-get update -qq
  step env DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends "${missing[@]}"
fi
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1" >> "$CLAUDE_ENV_FILE"
fi

step git config core.hooksPath .githooks
step dotnet tool restore
step dotnet restore ManagedCodecs.slnx

if [ ! -e samples/testsrc-420.avi ]; then
  echo "session-start: no generated samples; run tools/make-samples.sh once before dotnet test (about 6 minutes)."
fi
exit 0
