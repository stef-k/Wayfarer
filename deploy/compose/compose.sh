#!/bin/bash
# Bounded configuration preflight + raw Compose delegation, not a setup/lifecycle controller.
set -euo pipefail
fail() { echo "Compose configuration: $1" >&2; exit 2; }
[[ $# -ge 2 ]] || fail 'usage: compose.sh /absolute/deployment.env <compose arguments>'
config=$1
shift
[[ $config == /* && -f $config ]] || fail 'an absolute configuration file is required'
# Parse literals only; never execute the operator file or expand shell substitutions.
while IFS= read -r line || [[ -n $line ]]; do
    [[ -z $line || $line == \#* ]] && continue
    [[ $line =~ ^([A-Z_]+)=(.*)$ ]] || fail 'expected literal KEY=value'
    key=${BASH_REMATCH[1]}; value=${BASH_REMATCH[2]}
    case $key in
        PUBLIC_HOST|WAYFARER_DIGEST|PROXY_MODE|EDGE_PREFIX|DB_PASSWORD_FILE|DB_APP_PASSWORD_FILE|APP_PASSWORD_FILE|EXTERNAL_PROXY_ADDRESS|LOOPBACK_ADDRESS|LOOPBACK_PORT) ;;
        *) fail 'unknown configuration key' ;;
    esac
    [[ $value != *'$'* && $value != *'"'* && $value != *"'"* && $value != *'`'* ]] || fail 'values must be literal, without quoting/interpolation'
    export "$key=$value"
done < "$config"
[[ ${WAYFARER_DIGEST:-} =~ ^sha256:[0-9a-f]{64}$ ]] || fail 'immutable application sha256 digest required'
[[ ${PUBLIC_HOST:-} =~ ^[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9-]*[a-zA-Z0-9])?)+$ && ${#PUBLIC_HOST} -le 253 ]] || fail 'DNS hostname required'
[[ ${PROXY_MODE:-} == managed || ${PROXY_MODE:-} == external ]] || fail 'proxy mode must be managed or external'
# Keep addressing private and predictable; reserve .1/.3 and trust just the selected peer.
[[ ${EDGE_PREFIX:-172.30.64} =~ ^172\.(1[6-9]|2[0-9]|3[01])\.([0-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-5])$ ]] || fail 'edge prefix must name a private 172.16-31 /24'
for key in DB_PASSWORD_FILE DB_APP_PASSWORD_FILE APP_PASSWORD_FILE; do
    value=${!key:-}
    [[ $value == /* && -f $value && ! -L $value && -s $value ]] || fail "$key must name a nonempty regular absolute secret file"
    [[ $(stat -c %a "$value") == 600 ]] || fail "$key requires mode 0600"
done
bundle=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
# Ignore ambient Compose overrides/profiles; mode and file selection are explicit.
unset COMPOSE_FILE COMPOSE_PROFILES
files=(-f "$bundle/compose.yaml")
if [[ $PROXY_MODE == managed ]]; then
    files+=(--profile managed)
else
    [[ ${LOOPBACK_ADDRESS:-127.0.0.1} == 127.0.0.1 ]] || fail 'external endpoint must bind 127.0.0.1'
    [[ ${LOOPBACK_PORT:-8080} =~ ^[0-9]{1,5}$ ]] && (( 10#${LOOPBACK_PORT:-8080} >= 1 && 10#${LOOPBACK_PORT:-8080} <= 65535 )) || fail 'invalid loopback port'
    [[ ${EXTERNAL_PROXY_ADDRESS:-} == "${EDGE_PREFIX:-172.30.64}.1" ]] || fail 'external proxy peer must be the selected host bridge gateway'
    files+=(-f "$bundle/external.yaml")
fi
exec docker compose --env-file "$config" "${files[@]}" "$@"
