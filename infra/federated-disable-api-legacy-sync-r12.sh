#!/usr/bin/env bash
# Disables the legacy generic sync hosted by the API. The national IAAI job remains the only IAAI scheduler.
set -Eeuo pipefail

readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly EXPECTED_API_IMAGE="acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:inventory-search-r11-performance"
readonly EXPECTED_JOB_IMAGE="acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:iaai-resilience-r12"
readonly EXPECTED_CRON="15,45 * * * *"

fail() { printf 'DISABLE_API_LEGACY_SYNC_ERROR: %s\n' "$*" >&2; exit 1; }
app_field() { az containerapp show --name "$API_NAME" --resource-group "$RESOURCE_GROUP" --query "$1" --output tsv; }
job_field() { az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query "$1" --output tsv; }
fingerprint_app() {
  local digest
  az containerapp show --name "$API_NAME" --resource-group "$RESOURCE_GROUP" --query "$1" --output json \
    | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }
}
fingerprint_job() {
  local digest
  az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query "$1" --output json \
    | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }
}
api_env_value() {
  app_field "properties.template.containers[0].env[?name=='$1'].value | [0]"
}

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is unavailable"
az extension add --name containerapp --upgrade --only-show-errors

api_image_before="$(app_field 'properties.template.containers[0].image')"
api_identity_before="$(fingerprint_app identity)"
api_secrets_before="$(fingerprint_app properties.configuration.secrets)"
api_ingress_before="$(fingerprint_app properties.configuration.ingress)"
api_command_before="$(fingerprint_app 'properties.template.containers[0].command')"
api_min_replicas_before="$(app_field properties.template.scale.minReplicas)"

job_image_before="$(job_field 'properties.template.containers[0].image')"
job_trigger_before="$(job_field properties.configuration.triggerType)"
job_cron_before="$(job_field properties.configuration.scheduleTriggerConfig.cronExpression)"
job_identity_before="$(fingerprint_job identity)"
job_configuration_before="$(fingerprint_job properties.configuration)"
job_template_before="$(fingerprint_job properties.template)"

[[ "$api_image_before" == "$EXPECTED_API_IMAGE" ]] || fail "Expected API r11 image; found: ${api_image_before:-empty}"
[[ "$api_min_replicas_before" == "1" ]] || fail "Expected one warm API replica; found: ${api_min_replicas_before:-empty}"
[[ "$job_image_before" == "$EXPECTED_JOB_IMAGE" ]] || fail "Expected IAAI r12 image; found: ${job_image_before:-empty}"
[[ "$job_trigger_before" == "Schedule" ]] || fail "Expected IAAI Schedule trigger; found: ${job_trigger_before:-empty}"
[[ "$job_cron_before" == "$EXPECTED_CRON" ]] || fail "Expected IAAI cron ${EXPECTED_CRON}; found: ${job_cron_before:-empty}"

printf 'DISABLE_API_LEGACY_SYNC_UPDATE_START\n'
az containerapp update \
  --resource-group "$RESOURCE_GROUP" \
  --name "$API_NAME" \
  --set-env-vars Sync__Enabled=false Persistence__RunMigrations=false SearchProjection__WarmupOnStartup=true \
  --output none

api_image_after="$(app_field 'properties.template.containers[0].image')"
api_revision_after="$(app_field properties.latestRevisionName)"
api_identity_after="$(fingerprint_app identity)"
api_secrets_after="$(fingerprint_app properties.configuration.secrets)"
api_ingress_after="$(fingerprint_app properties.configuration.ingress)"
api_command_after="$(fingerprint_app 'properties.template.containers[0].command')"
api_min_replicas_after="$(app_field properties.template.scale.minReplicas)"

job_image_after="$(job_field 'properties.template.containers[0].image')"
job_trigger_after="$(job_field properties.configuration.triggerType)"
job_cron_after="$(job_field properties.configuration.scheduleTriggerConfig.cronExpression)"
job_identity_after="$(fingerprint_job identity)"
job_configuration_after="$(fingerprint_job properties.configuration)"
job_template_after="$(fingerprint_job properties.template)"

[[ "$api_image_after" == "$api_image_before" ]] || fail "API image changed unexpectedly"
[[ "$api_identity_after" == "$api_identity_before" ]] || fail "API identity changed unexpectedly"
[[ "$api_secrets_after" == "$api_secrets_before" ]] || fail "API secrets changed unexpectedly"
[[ "$api_ingress_after" == "$api_ingress_before" ]] || fail "API ingress changed unexpectedly"
[[ "$api_command_after" == "$api_command_before" ]] || fail "API command changed unexpectedly"
[[ "$api_min_replicas_after" == "$api_min_replicas_before" ]] || fail "API min replicas changed unexpectedly"
[[ "$(api_env_value Sync__Enabled)" == "false" ]] || fail "Sync__Enabled is not false on API"
[[ "$(api_env_value Persistence__RunMigrations)" == "false" ]] || fail "Persistence migrations are not disabled on API"

[[ "$job_image_after" == "$job_image_before" ]] || fail "IAAI job image changed unexpectedly"
[[ "$job_trigger_after" == "$job_trigger_before" ]] || fail "IAAI trigger changed unexpectedly"
[[ "$job_cron_after" == "$job_cron_before" ]] || fail "IAAI cron changed unexpectedly"
[[ "$job_identity_after" == "$job_identity_before" ]] || fail "IAAI identity changed unexpectedly"
[[ "$job_configuration_after" == "$job_configuration_before" ]] || fail "IAAI configuration changed unexpectedly"
[[ "$job_template_after" == "$job_template_before" ]] || fail "IAAI template changed unexpectedly"

printf 'DISABLE_API_LEGACY_SYNC_COMPLETED\n'
printf 'API_IMAGE=%s\nAPI_REVISION=%s\nAPI_SYNC_ENABLED=false\nAPI_MIN_REPLICAS=%s\n' "$api_image_after" "$api_revision_after" "$api_min_replicas_after"
printf 'IAAI_JOB_IMAGE=%s\nIAAI_TRIGGER=%s\nIAAI_CRON=%s\nIAAI_JOB_CHANGED=false\nIAAI_JOB_EXECUTION_STARTED=false\n' "$job_image_after" "$job_trigger_after" "$job_cron_after"
printf 'COPART_APIBARA_ENABLED=false\nMIGRATIONS_ENABLED=false\n'
