#!/usr/bin/env bash
# Enables the schedule only after the national manual transition and initial
# backfill are validated. PATCH uses Azure Resource Manager JSON Merge Patch so
# the existing job secrets, managed identity, registry configuration, command,
# and environment variables remain untouched.
set -Eeuo pipefail

readonly SUBSCRIPTION_ID="99edf3d1-a651-4d2d-af8e-d44a3360f584"
readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly JOB_NAME="job-lsc-iaai-pilot-prod"
readonly API_VERSION="2024-03-01"
readonly CRON_EXPRESSION="15,45 * * * *"
readonly REPLICA_TIMEOUT_SECONDS="1500"
readonly LEASE_MINUTES="28"

fail() {
  printf 'IAAI_SCHEDULE_ERROR: %s\n' "$*" >&2
  exit 1
}

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
az extension add --name containerapp --upgrade --only-show-errors

job_uri="https://management.azure.com/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.App/jobs/${JOB_NAME}?api-version=${API_VERSION}"
before_trigger="$(az rest --method get --uri "$job_uri" --query 'properties.configuration.triggerType' --output tsv)"
before_image="$(az rest --method get --uri "$job_uri" --query 'properties.template.containers[0].image' --output tsv)"

[[ "$before_trigger" == "Manual" ]] || fail "Expected Manual trigger before schedule activation; found: ${before_trigger:-empty}"
[[ "$before_image" == *":iaai-national-r3-full-backfill" ]] || fail "Expected national r3 full-backfill image before schedule activation; found: ${before_image:-empty}"

patch_body='{
  "properties": {
    "configuration": {
      "triggerType": "Schedule",
      "replicaTimeout": 1500,
      "replicaRetryLimit": 1,
      "scheduleTriggerConfig": {
        "cronExpression": "15,45 * * * *",
        "parallelism": 1,
        "replicaCompletionCount": 1
      }
    }
  }
}'

printf 'IAAI_SCHEDULE_PATCH_START cron=%s timeout_seconds=%s lease_minutes=%s\n' "$CRON_EXPRESSION" "$REPLICA_TIMEOUT_SECONDS" "$LEASE_MINUTES"
az rest --method patch --uri "$job_uri" --body "$patch_body" --output none

after_trigger="$(az rest --method get --uri "$job_uri" --query 'properties.configuration.triggerType' --output tsv)"
after_cron="$(az rest --method get --uri "$job_uri" --query 'properties.configuration.scheduleTriggerConfig.cronExpression' --output tsv)"
after_timeout="$(az rest --method get --uri "$job_uri" --query 'properties.configuration.replicaTimeout' --output tsv)"
after_parallelism="$(az rest --method get --uri "$job_uri" --query 'properties.configuration.scheduleTriggerConfig.parallelism' --output tsv)"
after_completion="$(az rest --method get --uri "$job_uri" --query 'properties.configuration.scheduleTriggerConfig.replicaCompletionCount' --output tsv)"
after_image="$(az rest --method get --uri "$job_uri" --query 'properties.template.containers[0].image' --output tsv)"

[[ "$after_trigger" == "Schedule" ]] || fail "Expected Schedule trigger after PATCH; found: ${after_trigger:-empty}"
[[ "$after_cron" == "$CRON_EXPRESSION" ]] || fail "Unexpected cron after PATCH: ${after_cron:-empty}"
[[ "$after_timeout" == "$REPLICA_TIMEOUT_SECONDS" ]] || fail "Unexpected timeout after PATCH: ${after_timeout:-empty}"
[[ "$after_parallelism" == "1" && "$after_completion" == "1" ]] || fail "Schedule concurrency controls were not applied"
[[ "$after_image" == "$before_image" ]] || fail "PATCH unexpectedly changed the job image"

printf 'IAAI_SCHEDULE_ENABLED\n'
printf 'TRIGGER=%s\n' "$after_trigger"
printf 'CRON=%s\n' "$after_cron"
printf 'REPLICA_TIMEOUT_SECONDS=%s\n' "$after_timeout"
printf 'PARALLELISM=%s\n' "$after_parallelism"
printf 'REPLICA_COMPLETION_COUNT=%s\n' "$after_completion"
printf 'JOB_IMAGE=%s\n' "$after_image"
printf 'LEASE_MINUTES=%s\n' "$LEASE_MINUTES"
printf 'COPART_APIBARA_ENABLED=false\n'
