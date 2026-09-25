#!/bin/bash
set -Eeuo pipefail

export PATH="$PATH:/home/debian/.dotnet"
export GIT_TERMINAL_PROMPT=0
BASE_DIR=/opt/tronloop/clusterpilot-engine
SERVICE=tronloop-clusterpilot-engine.service
APP=Tronloop.ClusterPilot.Engine
SOURCE_DIR="$BASE_DIR/source"
BRANCH=main
force=false
case "${1:-}" in
    --force) force=true ;;
    --help|-h) echo "Usage: $0 [--force]"; exit 0 ;;
    "") ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
esac
[[ $# -le 1 ]] || { echo "Too many arguments" >&2; exit 2; }

# Timer and manual runs must never replace a release at the same time.
exec 9>"$BASE_DIR/deploy.lock"
if $force; then
    echo "Forced deployment: waiting for any running deployment (up to 30 minutes)."
    flock -w 1800 9 || { echo "Deployment lock timed out." >&2; exit 1; }
else
    flock -n 9 || exit 0
fi
git -C "$SOURCE_DIR" fetch --quiet origin "$BRANCH"
commit=$(git -C "$SOURCE_DIR" rev-parse FETCH_HEAD)
deployed=$(cat "$BASE_DIR/deployed_commit.txt" 2>/dev/null || true)
if ! $force && [[ "$commit" == "$deployed" && -x "$BASE_DIR/current/$APP" ]]; then
    echo "Already deployed: $commit"
    exit 0
fi
echo "Publishing commit: $commit (force=$force)"

stage=$(mktemp -d "$BASE_DIR/.deploy.XXXXXXXX")
trap 'rm -rf -- "$stage"' EXIT
mkdir "$stage/source"
git -C "$SOURCE_DIR" archive "$commit" | tar -x -C "$stage/source"
dotnet publish "$stage/source/$APP.csproj" --disable-build-servers -c Release -r linux-arm \
    --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false \
    -o "$stage/publish"
test -x "$stage/publish/$APP"
if [[ -f /etc/tronloop/clusterpilot-engine/appsettings.Production.json ]]; then
    install -m 640 -o root -g debian \
        /etc/tronloop/clusterpilot-engine/appsettings.Production.json \
        "$stage/publish/appsettings.Production.json"
fi

# Keep the running version until publishing has completed successfully.
systemctl stop "$SERVICE"
if [[ -d "$BASE_DIR/current" ]]; then
    rm -rf -- "$BASE_DIR/previous"
    mv "$BASE_DIR/current" "$BASE_DIR/previous"
fi
rollback() {
    trap - ERR
    systemctl stop "$SERVICE" || true
    if [[ -d "$BASE_DIR/current" ]]; then
        mv "$BASE_DIR/current" "$BASE_DIR/failed-$(date +%Y%m%d-%H%M%S)"
    fi
    if [[ -d "$BASE_DIR/previous" ]]; then
        mv "$BASE_DIR/previous" "$BASE_DIR/current"
        systemctl start "$SERVICE" || true
    fi
    echo "Deployment failed; previous release restored if available." >&2
    exit 1
}
trap rollback ERR
mv "$stage/publish" "$BASE_DIR/current"
systemctl start "$SERVICE"
pid=$(systemctl show -p MainPID --value "$SERVICE")
sleep 15
systemctl is-active --quiet "$SERVICE"
test "$pid" -gt 0
test "$(systemctl show -p MainPID --value "$SERVICE")" = "$pid"
printf '%s\n' "$commit" > "$BASE_DIR/deployed_commit.txt.tmp"
mv "$BASE_DIR/deployed_commit.txt.tmp" "$BASE_DIR/deployed_commit.txt"
trap - ERR
echo "Deployment completed: $commit"
