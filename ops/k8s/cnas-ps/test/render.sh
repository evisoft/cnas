#!/usr/bin/env bash
# =============================================================================
# render.sh — landmark-string assertion harness for the cnas-ps chart.
# =============================================================================
# Renders the chart against the default + staging + production values
# files, then greps the rendered YAML for KEY landmark strings that
# MUST appear if the chart was templated correctly.
#
# Per CLAUDE.md (RULE 1) — TDD does not apply to infra-as-code, but we
# enforce render-time guarantees with this harness. Non-zero exit on
# any missing landmark.
#
# Requires `helm` on PATH. If `helm` is missing the script exits 0 with
# a diagnostic message — CI will independently run `helm lint` +
# `helm template` and surface real failures there.
# =============================================================================
set -euo pipefail

CHART_DIR="$(cd "$(dirname "$0")/.." && pwd)"
RELEASE_NAME="render-test"
TMPDIR="$(mktemp -d)"
trap 'rm -rf "$TMPDIR"' EXIT

if ! command -v helm >/dev/null 2>&1; then
    echo "[render.sh] helm not installed — skipping (CI will catch this)." >&2
    exit 0
fi

render() {
    local label="$1"
    local out="$TMPDIR/$label.yaml"
    shift
    echo "[render.sh] rendering $label..."
    helm template "$RELEASE_NAME" "$CHART_DIR" \
        --set image.tag=test-tag \
        --set ingress.host=test.example.gov.md \
        "$@" >"$out"
    echo "$out"
}

assert_in() {
    local file="$1"
    local needle="$2"
    if ! grep -q -- "$needle" "$file"; then
        echo "[render.sh] FAIL: '$needle' not found in $(basename "$file")" >&2
        return 1
    fi
}

DEFAULT_OUT="$(render default)"
STAGING_OUT="$(render staging -f "$CHART_DIR/values.staging.yaml")"
PROD_OUT="$(render production -f "$CHART_DIR/values.production.yaml")"

# ----- Default values landmarks --------------------------------------------
assert_in "$DEFAULT_OUT" "kind: Deployment"
assert_in "$DEFAULT_OUT" "kind: HorizontalPodAutoscaler"
assert_in "$DEFAULT_OUT" "kind: StatefulSet"
assert_in "$DEFAULT_OUT" "kind: Ingress"
assert_in "$DEFAULT_OUT" "kind: NetworkPolicy"
assert_in "$DEFAULT_OUT" "kind: ServiceAccount"
assert_in "$DEFAULT_OUT" "kind: PodDisruptionBudget"
assert_in "$DEFAULT_OUT" "cnas-ps-api"
assert_in "$DEFAULT_OUT" "image:.*cnas-ps-api:test-tag"
assert_in "$DEFAULT_OUT" "path: /health/live"
assert_in "$DEFAULT_OUT" "path: /health/ready"
assert_in "$DEFAULT_OUT" "spilo-16"
assert_in "$DEFAULT_OUT" "minio"
assert_in "$DEFAULT_OUT" "test.example.gov.md"

# ----- Staging overlay landmarks -------------------------------------------
assert_in "$STAGING_OUT" "cnas-ps.staging.example.gov.md"
assert_in "$STAGING_OUT" "letsencrypt-staging"
assert_in "$STAGING_OUT" "kind: Secret"

# ----- Production overlay landmarks ----------------------------------------
assert_in "$PROD_OUT" "cnas-ps.example.gov.md"
assert_in "$PROD_OUT" "kind: ExternalSecret"
assert_in "$PROD_OUT" "cnas-prod-vault"
assert_in "$PROD_OUT" "requiredDuringSchedulingIgnoredDuringExecution"

echo "[render.sh] OK — all landmark assertions passed."
