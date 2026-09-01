#!/usr/bin/env bash
# Configures bounded national IAAI backfill batches without rebuilding or starting the job.
set -Eeuo pipefail

readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly REGISTRY_LOGIN_SERVER="acrlscinvprodeus2.azurecr.io"
readonly IMAGE_REPOSITORY="lsc-inventory-engine"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly EXPECTED_API_IMAGE="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:inventory-search-r11-performance"
readonly EXPECTED_JOB_IMAGE="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:iaai-cursor-recovery-r14"
readonly EXPECTED_CRON="15,45 * * * *"
readonly BACKFILL_PAGES_PER_RUN="80"
readonly BACKFILL_MAX_REQUESTS_PER_RUN="120"

fail() { printf 'IAAI_R15_CONFIG_ERROR: %s\n' "$*" >&2; exit 1; }
job_field() { az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query "$1" --output tsv; }
app_field() { az containerapp show --name "$API_NAME" --resource-group "$RESOURCE_GROUP" --query "$1" --output tsv; }
fingerprint_job() {
  local digest
  az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query "$1" --output json \
    | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }
}
fingerprint_app() {
  local digest
  az containerapp show --name "$API_NAME" --resource-group "$RESOURCE_GROUP" --query "$1" --output json \
    | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }
}
job_env_value() { job_field "properties.template.containers[0].env[?name=='$1'].value | [0]"; }
app_env_value() { app_field "properties.template.containers[0].env[?name=='$1'].value | [0]"; }

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is unavailable"
az extension add --name containerapp --upgrade --only-show-errors

api_image_before="$(app_field 'properties.template.containers[0].image')"
api_revision_before="$(app_field properties.latestRevisionName)"
api_identity_before="$(fingerprint_app identity)"
api_secrets_before="$(fingerprint_app properties.configuration.secrets)"
api_ingress_before="$(fingerprint_app properties.configuration.ingress)"

job_image_before="$(job_field 'properties.template.containers[0].image')"
trigger_before="$(job_field properties.configuration.triggerType)"
cron_before="$(job_field properties.configuration.scheduleTriggerConfig.cronExpression)"
timeout_before="$(job_field properties.configuration.replicaTimeout)"
retry_limit_before="$(job_field properties.configuration.replicaRetryLimit)"
parallelism_before="$(job_field properties.configuration.scheduleTriggerConfig.parallelism)"
completion_before="$(job_field properties.configuration.scheduleTriggerConfig.replicaCompletionCount)"
identity_before="$(fingerprint_job identity)"
command_before="$(fingerprint_job 'properties.template.containers[0].command')"
secrets_before="$(fingerprint_job properties.configuration.secrets)"

[[ "$api_image_before" == "$EXPECTED_API_IMAGE" ]] || fail "Expected API r11 image; found: ${api_image_before:-empty}"
[[ "$(app_env_value Sync__Enabled)" == "false" ]] || fail "Legacy API sync must remain disabled"
[[ "$job_image_before" == "$EXPECTED_JOB_IMAGE" ]] || fail "Expected IAAI job r14 image; found: ${job_image_before:-empty}"
[[ "$trigger_before" == "Schedule" ]] || fail "Expected Schedule trigger; found: ${trigger_before:-empty}"
[[ "$cron_before" == "$EXPECTED_CRON" ]] || fail "Expected cron ${EXPECTED_CRON}; found: ${cron_before:-empty}"
[[ "$timeout_before" == "1500" ]] || fail "Expected timeout 1500; found: ${timeout_before:-empty}"
[[ "$retry_limit_before" == "1" ]] || fail "Expected retry limit 1; found: ${retry_limit_before:-empty}"
[[ "$(job_env_value IaaIPilot__Enabled)" == "false" ]] || fail "IAAI pilot must remain disabled"
[[ "$(job_env_value IaaIPilot__RunOnStartup)" == "false" ]] || fail "IAAI pilot startup must remain disabled"
[[ "$(job_env_value IaaINational__Enabled)" == "true" ]] || fail "National IAAI must remain enabled"
[[ "$(job_env_value IaaINational__RunOnStartup)" == "true" ]] || fail "National IAAI startup must remain enabled"
[[ "$(job_env_value Persistence__RunMigrations)" == "false" ]] || fail "Persistence migrations must remain disabled"

printf 'IAAI_R15_BOUNDED_BACKFILL_UPDATE_START pages=%s requests=%s\n' "$BACKFILL_PAGES_PER_RUN" "$BACKFILL_MAX_REQUESTS_PER_RUN"
az containerapp job update \
  --resource-group "$RESOURCE_GROUP" \
  --name "$IAAI_JOB_NAME" \
  --set-env-vars \
    IaaINational__BackfillPagesPerRun="$BACKFILL_PAGES_PER_RUN" \
    IaaINational__BackfillMaxRequestsPerRun="$BACKFILL_MAX_REQUESTS_PER_RUN" \
    Persistence__RunMigrations=false \
  --output none

api_image_after="$(app_field 'properties.template.containers[0].image')"
api_revision_after="$(app_field properties.latestRevisionName)"
api_identity_after="$(fingerprint_app identity)"
api_secrets_after="$(fingerprint_app properties.configuration.secrets)"
api_ingress_after="$(fingerprint_app properties.configuration.ingress)"

job_image_after="$(job_field 'properties.template.containers[0].image')"
trigger_after="$(job_field properties.configuration.triggerType)"
cron_after="$(job_field properties.configuration.scheduleTriggerConfig.cronExpression)"
timeout_after="$(job_field properties.configuration.replicaTimeout)"
retry_limit_after="$(job_field properties.configuration.replicaRetryLimit)"
parallelism_after="$(job_field properties.configuration.scheduleTriggerConfig.parallelism)"
completion_after="$(job_field properties.configuration.scheduleTriggerConfig.replicaCompletionCount)"
identity_after="$(fingerprint_job identity)"
command_after="$(fingerprint_job 'properties.template.containers[0].command')"
secrets_after="$(fingerprint_job properties.configuration.secrets)"

[[ "$api_image_after" == "$api_image_before" ]] || fail "API image changed unexpectedly"
[[ "$api_revision_after" == "$api_revision_before" ]] || fail "API revision changed unexpectedly"
[[ "$api_identity_after" == "$api_identity_before" ]] || fail "API identity changed unexpectedly"
[[ "$api_secrets_after" == "$api_secrets_before" ]] || fail "API secrets changed unexpectedly"
[[ "$api_ingress_after" == "$api_ingress_before" ]] || fail "API ingress changed unexpectedly"
[[ "$(app_env_value Sync__Enabled)" == "false" ]] || fail "Legacy API sync was re-enabled"

[[ "$job_image_after" == "$job_image_before" ]] || fail "IAAI image changed unexpectedly"
[[ "$trigger_after" == "$trigger_before" ]] || fail "IAAI trigger changed unexpectedly"
[[ "$cron_after" == "$cron_before" ]] || fail "IAAI cron changed unexpectedly"
[[ "$timeout_after" == "$timeout_before" ]] || fail "IAAI timeout changed unexpectedly"
[[ "$retry_limit_after" == "$retry_limit_before" ]] || fail "IAAI retry limit changed unexpectedly"
[[ "$parallelism_after" == "$parallelism_before" ]] || fail "IAAI parallelism changed unexpectedly"
[[ "$completion_after" == "$completion_before" ]] || fail "IAAI completion count changed unexpectedly"
[[ "$identity_after" == "$identity_before" ]] || fail "IAAI identity changed unexpectedly"
[[ "$command_after" == "$command_before" ]] || fail "IAAI command changed unexpectedly"
[[ "$secrets_after" == "$secrets_before" ]] || fail "IAAI secrets changed unexpectedly"

[[ "$(job_env_value IaaINational__BackfillPagesPerRun)" == "$BACKFILL_PAGES_PER_RUN" ]] || fail "Backfill page budget was not applied"
[[ "$(job_env_value IaaINational__BackfillMaxRequestsPerRun)" == "$BACKFILL_MAX_REQUESTS_PER_RUN" ]] || fail "Backfill request budget was not applied"
[[ "$(job_env_value IaaIPilot__Enabled)" == "false" ]] || fail "IAAI pilot was re-enabled"
[[ "$(job_env_value IaaIPilot__RunOnStartup)" == "false" ]] || fail "IAAI pilot startup was re-enabled"
[[ "$(job_env_value IaaINational__Enabled)" == "true" ]] || fail "National IAAI was disabled"
[[ "$(job_env_value IaaINational__RunOnStartup)" == "true" ]] || fail "National IAAI startup was disabled"
[[ "$(job_env_value Persistence__RunMigrations)" == "false" ]] || fail "Persistence migrations were enabled"

printf 'IAAI_R15_CONFIG_COMPLETED\n'
printf 'API_IMAGE=%s\nAPI_REVISION=%s\nAPI_CHANGED=false\nAPI_LEGACY_SYNC_ENABLED=false\n' "$api_image_after" "$api_revision_after"
printf 'IAAI_JOB_IMAGE=%s\nTRIGGER=%s\nCRON=%s\nTIMEOUT=%s\nAZURE_RETRY_LIMIT=%s\n' "$job_image_after" "$trigger_after" "$cron_after" "$timeout_after" "$retry_limit_after"
printf 'BACKFILL_PAGES_PER_RUN=%s\nBACKFILL_MAX_REQUESTS_PER_RUN=%s\n' "$BACKFILL_PAGES_PER_RUN" "$BACKFILL_MAX_REQUESTS_PER_RUN"
printf 'IAAI_JOB_EXECUTION_STARTED=false\nCOPART_APIBARA_ENABLED=false\nMIGRATIONS_ENABLED=false\n'
