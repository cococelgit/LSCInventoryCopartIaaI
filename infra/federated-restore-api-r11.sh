#!/usr/bin/env bash
# Restores only the public Inventory API to the already deployed r11 image.
# Does not build an image, start a job, alter a schedule, or enable migrations.
set -Eeuo pipefail

readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly RECOVERY_IMAGE="acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:inventory-search-r11-performance"
readonly EXPECTED_CRON="15,45 * * * *"

fail() { printf 'API_R11_RESTORE_ERROR: %s\n' "$*" >&2; exit 1; }
fingerprint() {
  local resource_kind="$1" resource_name="$2" query="$3" digest
  if [[ "$resource_kind" == "app" ]]; then
    az containerapp show --resource-group "$RESOURCE_GROUP" --name "$resource_name" --query "$query" --output json
  else
    az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$resource_name" --query "$query" --output json
  fi | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }
}

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is unavailable"
az extension add --name containerapp --upgrade --only-show-errors

api_image_before="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query 'properties.template.containers[0].image' --output tsv)"
api_revision_before="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.latestRevisionName --output tsv)"
api_ready_before="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.latestReadyRevisionName --output tsv)"
api_mode_before="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.configuration.activeRevisionsMode --output tsv)"
api_identity_before="$(fingerprint app "$API_NAME" identity)"
api_secrets_before="$(fingerprint app "$API_NAME" properties.configuration.secrets)"
api_ingress_before="$(fingerprint app "$API_NAME" properties.configuration.ingress)"

job_image_before="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query 'properties.template.containers[0].image' --output tsv)"
job_trigger_before="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query properties.configuration.triggerType --output tsv)"
job_cron_before="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query properties.configuration.scheduleTriggerConfig.cronExpression --output tsv)"
job_timeout_before="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query properties.configuration.replicaTimeout --output tsv)"
job_retry_before="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query properties.configuration.replicaRetryLimit --output tsv)"
job_identity_before="$(fingerprint job "$IAAI_JOB_NAME" identity)"
job_command_before="$(fingerprint job "$IAAI_JOB_NAME" 'properties.template.containers[0].command')"
job_environment_before="$(fingerprint job "$IAAI_JOB_NAME" 'properties.template.containers[0].env')"
job_secrets_before="$(fingerprint job "$IAAI_JOB_NAME" properties.configuration.secrets)"

[[ "$api_mode_before" == "Single" ]] || fail "Expected API Single revision mode; found: ${api_mode_before:-empty}"
[[ "$job_image_before" == "$RECOVERY_IMAGE" ]] || fail "Expected IAAI job r11 image; found: ${job_image_before:-empty}"
[[ "$job_trigger_before" == "Schedule" ]] || fail "Expected IAAI Schedule trigger; found: ${job_trigger_before:-empty}"
[[ "$job_cron_before" == "$EXPECTED_CRON" ]] || fail "Expected IAAI cron ${EXPECTED_CRON}; found: ${job_cron_before:-empty}"
[[ "$job_timeout_before" == "1500" ]] || fail "Expected IAAI timeout 1500; found: ${job_timeout_before:-empty}"
[[ "$job_retry_before" == "1" ]] || fail "Expected IAAI retry limit 1; found: ${job_retry_before:-empty}"

printf 'API_R11_RESTORE_START\n'
printf 'API_IMAGE_BEFORE=%s\nAPI_REVISION_BEFORE=%s\nAPI_READY_BEFORE=%s\n' "$api_image_before" "$api_revision_before" "$api_ready_before"
az containerapp update \
  --resource-group "$RESOURCE_GROUP" \
  --name "$API_NAME" \
  --image "$RECOVERY_IMAGE" \
  --min-replicas 1 \
  --set-env-vars Persistence__RunMigrations=false SearchProjection__WarmupOnStartup=true \
  --output none

api_image_after="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query 'properties.template.containers[0].image' --output tsv)"
api_revision_after="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.latestRevisionName --output tsv)"
api_ready_after="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.latestReadyRevisionName --output tsv)"
api_mode_after="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.configuration.activeRevisionsMode --output tsv)"
api_min_replicas_after="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.template.scale.minReplicas --output tsv)"
api_identity_after="$(fingerprint app "$API_NAME" identity)"
api_secrets_after="$(fingerprint app "$API_NAME" properties.configuration.secrets)"
api_ingress_after="$(fingerprint app "$API_NAME" properties.configuration.ingress)"
api_state_after="$(az containerapp revision list --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "[?name=='${api_revision_after}'].properties.runningState | [0]" --output tsv)"

job_image_after="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query 'properties.template.containers[0].image' --output tsv)"
job_trigger_after="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query properties.configuration.triggerType --output tsv)"
job_cron_after="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query properties.configuration.scheduleTriggerConfig.cronExpression --output tsv)"
job_identity_after="$(fingerprint job "$IAAI_JOB_NAME" identity)"
job_command_after="$(fingerprint job "$IAAI_JOB_NAME" 'properties.template.containers[0].command')"
job_environment_after="$(fingerprint job "$IAAI_JOB_NAME" 'properties.template.containers[0].env')"
job_secrets_after="$(fingerprint job "$IAAI_JOB_NAME" properties.configuration.secrets)"

[[ "$api_image_after" == "$RECOVERY_IMAGE" ]] || fail "API image was not restored to r11"
[[ "$api_mode_after" == "$api_mode_before" ]] || fail "API revision mode changed unexpectedly"
[[ "$api_min_replicas_after" == "1" ]] || fail "API min replicas is not 1"
[[ "$api_identity_after" == "$api_identity_before" ]] || fail "API identity changed unexpectedly"
[[ "$api_secrets_after" == "$api_secrets_before" ]] || fail "API secrets changed unexpectedly"
[[ "$api_ingress_after" == "$api_ingress_before" ]] || fail "API ingress changed unexpectedly"
[[ "$job_image_after" == "$job_image_before" ]] || fail "IAAI job image changed unexpectedly"
[[ "$job_trigger_after" == "$job_trigger_before" ]] || fail "IAAI trigger changed unexpectedly"
[[ "$job_cron_after" == "$job_cron_before" ]] || fail "IAAI cron changed unexpectedly"
[[ "$job_identity_after" == "$job_identity_before" ]] || fail "IAAI identity changed unexpectedly"
[[ "$job_command_after" == "$job_command_before" ]] || fail "IAAI command changed unexpectedly"
[[ "$job_environment_after" == "$job_environment_before" ]] || fail "IAAI environment changed unexpectedly"
[[ "$job_secrets_after" == "$job_secrets_before" ]] || fail "IAAI secrets changed unexpectedly"

printf 'API_R11_RESTORE_COMPLETED\n'
printf 'API_IMAGE=%s\nAPI_REVISION=%s\nAPI_READY_REVISION=%s\nAPI_STATE=%s\nAPI_MIN_REPLICAS=%s\n' "$api_image_after" "$api_revision_after" "$api_ready_after" "$api_state_after" "$api_min_replicas_after"
printf 'IAAI_JOB_IMAGE=%s\nTRIGGER=%s\nCRON=%s\nIAAI_JOB_EXECUTION_STARTED=false\n' "$job_image_after" "$job_trigger_after" "$job_cron_after"
printf 'COPART_APIBARA_ENABLED=false\nMIGRATIONS_ENABLED=false\n'
