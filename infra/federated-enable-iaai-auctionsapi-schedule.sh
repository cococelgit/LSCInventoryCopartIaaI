#!/usr/bin/env bash
# Activates the IAAI scheduler only after the AuctionsAPI initial import has
# completed. The job wakes hourly in UTC; the application guard executes only
# at 07:00, 09:00, ..., 23:00 America/New_York, so DST never shifts the window.
set -Eeuo pipefail

readonly SUBSCRIPTION_ID="99edf3d1-a651-4d2d-af8e-d44a3360f584"
readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly JOB_NAME="job-lsc-iaai-pilot-prod"
readonly API_VERSION="2024-03-01"
readonly CRON_EXPRESSION="0 * * * *"
readonly REPLICA_TIMEOUT_SECONDS="1500"
readonly PARALLELISM="1"
readonly COMPLETION_COUNT="1"

fail() { printf 'IAAI_AUCTIONSAPI_SCHEDULE_ERROR: %s\n' "$*" >&2; exit 1; }
command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
az extension add --name containerapp --upgrade --only-show-errors

job_uri="https://management.azure.com/subscriptions/${SUBSCRIPTION_ID}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.App/jobs/${JOB_NAME}?api-version=${API_VERSION}"
before="$(az rest --method get --uri "$job_uri")"
before_trigger="$(jq -r '.properties.configuration.triggerType // ""' <<<"$before")"
before_image="$(jq -r '.properties.template.containers[0].image // ""' <<<"$before")"
before_cron="$(jq -r '.properties.configuration.scheduleTriggerConfig.cronExpression // ""' <<<"$before")"

[[ "$before_trigger" == "Manual" || "$before_trigger" == "Schedule" ]] || fail "Unexpected trigger: ${before_trigger:-empty}"
[[ "$before_image" == *"auctionsapi"* || "$before_image" == *"api-"* ]] || fail "Job image does not identify the API release: ${before_image:-empty}"

patch_body="$(jq -n \
  --arg cron "$CRON_EXPRESSION" \
  --argjson timeout "$REPLICA_TIMEOUT_SECONDS" \
  --argjson parallelism "$PARALLELISM" \
  --argjson completion "$COMPLETION_COUNT" \
  '{properties:{configuration:{triggerType:"Schedule",replicaTimeout:$timeout,replicaRetryLimit:1,scheduleTriggerConfig:{cronExpression:$cron,parallelism:$parallelism,replicaCompletionCount:$completion}}}}')"

printf 'IAAI_AUCTIONSAPI_SCHEDULE_PATCH_START old_trigger=%s old_cron=%s new_cron=%s\n' "$before_trigger" "$before_cron" "$CRON_EXPRESSION"
az rest --method patch --uri "$job_uri" --body "$patch_body" --output none

after="$(az rest --method get --uri "$job_uri")"
after_trigger="$(jq -r '.properties.configuration.triggerType // ""' <<<"$after")"
after_cron="$(jq -r '.properties.configuration.scheduleTriggerConfig.cronExpression // ""' <<<"$after")"
after_parallelism="$(jq -r '.properties.configuration.scheduleTriggerConfig.parallelism // ""' <<<"$after")"
after_completion="$(jq -r '.properties.configuration.scheduleTriggerConfig.replicaCompletionCount // ""' <<<"$after")"
after_image="$(jq -r '.properties.template.containers[0].image // ""' <<<"$after")"

[[ "$after_trigger" == "Schedule" ]] || fail "Schedule trigger was not applied"
[[ "$after_cron" == "$CRON_EXPRESSION" ]] || fail "Unexpected cron: ${after_cron:-empty}"
[[ "$after_parallelism" == "$PARALLELISM" && "$after_completion" == "$COMPLETION_COUNT" ]] || fail "Concurrency guard was not applied"
[[ "$after_image" == "$before_image" ]] || fail "The schedule patch changed the image unexpectedly"

printf 'IAAI_AUCTIONSAPI_SCHEDULE_READY\nTRIGGER=%s\nCRON_UTC=%s\nWINDOW_TIMEZONE=America/New_York\nWINDOW=07:00-23:00\nINTERVAL_HOURS=2\nPARALLELISM=%s\nREPLICA_COMPLETION_COUNT=%s\nJOB_IMAGE=%s\n' "$after_trigger" "$after_cron" "$after_parallelism" "$after_completion" "$after_image"
