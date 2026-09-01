#!/usr/bin/env bash
# Builds the asynchronous title-projection rebuild API revision and changes only the shared API Container App.
set -Eeuo pipefail

readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly REGISTRY_NAME="acrlscinvprodeus2"
readonly REGISTRY_LOGIN_SERVER="acrlscinvprodeus2.azurecr.io"
readonly IMAGE_REPOSITORY="lsc-inventory-engine"
readonly IMAGE_TAG="inventory-api-integrated-r25-rebuild-async"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly COPART_JOB_NAME="job-lsc-copart-excel-prod"
readonly COPART_AUTO_JOB_NAME="job-lsc-copart-auto-prod"
readonly GENERIC_JOB_NAME="job-lsc-inventory-ingestion-prod"
readonly EXPECTED_API_IMAGE="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:inventory-api-integrated-r24-copart-title-mapping"
readonly EXPECTED_IAAI_IMAGE="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:iaai-cursor-recovery-r14"
readonly EXPECTED_IAAI_CRON="15,45 * * * *"
readonly EXPECTED_COPART_AUTO_ARGS='["--copart-excel-run"]'
readonly SOURCE_CONTEXT_URL="https://lsc-inv-revi-zyn4tlbw.manus.space/manus-storage/lsc-inventory-engine-api-r25-rebuild-async.tar_dc2fa24a.gz"
readonly SOURCE_CONTEXT_SHA256="ab2abf7f1857a847b40f0bba87fccc0f81976a8c41805b43f278e257ee093daa"

fail() { printf 'API_R25_REBUILD_ASYNC_DEPLOY_ERROR: %s\n' "$*" >&2; exit 1; }
app_field() { az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "$1" --output tsv; }
job_field() { az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$1" --query "$2" --output tsv; }
fingerprint_app() {
  local digest
  az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "$1" --output json | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }
}
fingerprint_job() {
  local job_name="$1" query="$2" digest
  az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$job_name" --query "$query" --output json | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }
}
app_env_value() { app_field "properties.template.containers[0].env[?name=='$1'].value | [0]"; }

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
command -v curl >/dev/null 2>&1 || fail "curl is unavailable"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is unavailable"
az extension add --name containerapp --upgrade --only-show-errors

api_image_before="$(app_field 'properties.template.containers[0].image')"
api_mode_before="$(app_field properties.configuration.activeRevisionsMode)"
api_identity_before="$(fingerprint_app identity)"
api_secrets_before="$(fingerprint_app properties.configuration.secrets)"
api_ingress_before="$(fingerprint_app properties.configuration.ingress)"
api_scale_before="$(fingerprint_app properties.template.scale)"
api_sync_before="$(app_env_value Sync__Enabled)"
api_migrations_before="$(app_env_value Persistence__RunMigrations)"
api_warmup_before="$(app_env_value SearchProjection__WarmupOnStartup)"

iaai_image_before="$(job_field "$IAAI_JOB_NAME" 'properties.template.containers[0].image')"
iaai_trigger_before="$(job_field "$IAAI_JOB_NAME" properties.configuration.triggerType)"
iaai_cron_before="$(job_field "$IAAI_JOB_NAME" properties.configuration.scheduleTriggerConfig.cronExpression)"
iaai_template_before="$(fingerprint_job "$IAAI_JOB_NAME" properties.template)"
iaai_identity_before="$(fingerprint_job "$IAAI_JOB_NAME" identity)"
iaai_secrets_before="$(fingerprint_job "$IAAI_JOB_NAME" properties.configuration.secrets)"
copart_template_before="$(fingerprint_job "$COPART_JOB_NAME" properties.template)"
copart_configuration_before="$(fingerprint_job "$COPART_JOB_NAME" properties.configuration)"
copart_identity_before="$(fingerprint_job "$COPART_JOB_NAME" identity)"
copart_auto_args_before="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$COPART_AUTO_JOB_NAME" --query 'properties.template.containers[0].args' --output json | tr -d '[:space:]')"
copart_auto_template_before="$(fingerprint_job "$COPART_AUTO_JOB_NAME" properties.template)"
copart_auto_configuration_before="$(fingerprint_job "$COPART_AUTO_JOB_NAME" properties.configuration)"
copart_auto_identity_before="$(fingerprint_job "$COPART_AUTO_JOB_NAME" identity)"
generic_trigger_before="$(job_field "$GENERIC_JOB_NAME" properties.configuration.triggerType)"
generic_template_before="$(fingerprint_job "$GENERIC_JOB_NAME" properties.template)"
generic_configuration_before="$(fingerprint_job "$GENERIC_JOB_NAME" properties.configuration)"
generic_identity_before="$(fingerprint_job "$GENERIC_JOB_NAME" identity)"

[[ "$api_image_before" == "$EXPECTED_API_IMAGE" ]] || fail "Expected r24 API image; found: ${api_image_before:-empty}"
[[ "$api_mode_before" == "Single" ]] || fail "Expected API Single revision mode; found: ${api_mode_before:-empty}"
[[ "$api_sync_before" == "false" ]] || fail "Expected Sync__Enabled=false; found: ${api_sync_before:-empty}"
[[ "$api_migrations_before" == "false" ]] || fail "Expected Persistence__RunMigrations=false; found: ${api_migrations_before:-empty}"
[[ "$api_warmup_before" == "true" ]] || fail "Expected SearchProjection__WarmupOnStartup=true; found: ${api_warmup_before:-empty}"
[[ "$iaai_image_before" == "$EXPECTED_IAAI_IMAGE" ]] || fail "Expected IAAI r14 image; found: ${iaai_image_before:-empty}"
[[ "$iaai_trigger_before" == "Schedule" && "$iaai_cron_before" == "$EXPECTED_IAAI_CRON" ]] || fail "IAAI trigger/cron changed unexpectedly"
[[ "$copart_auto_args_before" == "$EXPECTED_COPART_AUTO_ARGS" ]] || fail "Expected Copart auto arguments ${EXPECTED_COPART_AUTO_ARGS}; found: ${copart_auto_args_before:-empty}"
[[ "$generic_trigger_before" == "Manual" ]] || fail "Expected generic job to remain Manual; found: ${generic_trigger_before:-empty}"

temporary_source="$(mktemp)"
trap 'rm -f "$temporary_source"' EXIT
curl --fail --silent --show-error --location --retry 3 --retry-delay 2 "$SOURCE_CONTEXT_URL" --output "$temporary_source"
read -r source_hash _ < <(sha256sum "$temporary_source")
[[ "$source_hash" == "$SOURCE_CONTEXT_SHA256" ]] || fail "Source checksum mismatch"

image_ref="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:${IMAGE_TAG}"
printf 'API_R25_BUILD_START image=%s source_sha256=%s\n' "$image_ref" "$SOURCE_CONTEXT_SHA256"
az acr build --registry "$REGISTRY_NAME" --image "${IMAGE_REPOSITORY}:${IMAGE_TAG}" --file Dockerfile "$SOURCE_CONTEXT_URL"
printf 'API_R25_UPDATE_START\n'
az containerapp update --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --image "$image_ref" --output none

api_image_after="$(app_field 'properties.template.containers[0].image')"
api_revision_after="$(app_field properties.latestRevisionName)"
api_ready_after="$(app_field properties.latestReadyRevisionName)"
api_mode_after="$(app_field properties.configuration.activeRevisionsMode)"
api_identity_after="$(fingerprint_app identity)"
api_secrets_after="$(fingerprint_app properties.configuration.secrets)"
api_ingress_after="$(fingerprint_app properties.configuration.ingress)"
api_scale_after="$(fingerprint_app properties.template.scale)"
api_sync_after="$(app_env_value Sync__Enabled)"
api_migrations_after="$(app_env_value Persistence__RunMigrations)"
api_warmup_after="$(app_env_value SearchProjection__WarmupOnStartup)"
api_state_after="$(az containerapp revision list --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "[?name=='${api_revision_after}'].properties.runningState | [0]" --output tsv)"
api_fqdn="$(app_field properties.configuration.ingress.fqdn)"
iaai_image_after="$(job_field "$IAAI_JOB_NAME" 'properties.template.containers[0].image')"
iaai_trigger_after="$(job_field "$IAAI_JOB_NAME" properties.configuration.triggerType)"
iaai_cron_after="$(job_field "$IAAI_JOB_NAME" properties.configuration.scheduleTriggerConfig.cronExpression)"
iaai_template_after="$(fingerprint_job "$IAAI_JOB_NAME" properties.template)"
iaai_identity_after="$(fingerprint_job "$IAAI_JOB_NAME" identity)"
iaai_secrets_after="$(fingerprint_job "$IAAI_JOB_NAME" properties.configuration.secrets)"
copart_template_after="$(fingerprint_job "$COPART_JOB_NAME" properties.template)"
copart_configuration_after="$(fingerprint_job "$COPART_JOB_NAME" properties.configuration)"
copart_identity_after="$(fingerprint_job "$COPART_JOB_NAME" identity)"
copart_auto_args_after="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$COPART_AUTO_JOB_NAME" --query 'properties.template.containers[0].args' --output json | tr -d '[:space:]')"
copart_auto_template_after="$(fingerprint_job "$COPART_AUTO_JOB_NAME" properties.template)"
copart_auto_configuration_after="$(fingerprint_job "$COPART_AUTO_JOB_NAME" properties.configuration)"
copart_auto_identity_after="$(fingerprint_job "$COPART_AUTO_JOB_NAME" identity)"
generic_trigger_after="$(job_field "$GENERIC_JOB_NAME" properties.configuration.triggerType)"
generic_template_after="$(fingerprint_job "$GENERIC_JOB_NAME" properties.template)"
generic_configuration_after="$(fingerprint_job "$GENERIC_JOB_NAME" properties.configuration)"
generic_identity_after="$(fingerprint_job "$GENERIC_JOB_NAME" identity)"

[[ "$api_image_after" == "$image_ref" ]] || fail "API image was not updated to r25"
[[ "$api_mode_after" == "$api_mode_before" && "$api_identity_after" == "$api_identity_before" && "$api_secrets_after" == "$api_secrets_before" && "$api_ingress_after" == "$api_ingress_before" && "$api_scale_after" == "$api_scale_before" && "$api_sync_after" == "$api_sync_before" && "$api_migrations_after" == "$api_migrations_before" && "$api_warmup_after" == "$api_warmup_before" ]] || fail "API configuration changed unexpectedly"
[[ "$iaai_image_after" == "$iaai_image_before" && "$iaai_trigger_after" == "$iaai_trigger_before" && "$iaai_cron_after" == "$iaai_cron_before" && "$iaai_template_after" == "$iaai_template_before" && "$iaai_identity_after" == "$iaai_identity_before" && "$iaai_secrets_after" == "$iaai_secrets_before" ]] || fail "IAAI job changed unexpectedly"
[[ "$copart_template_after" == "$copart_template_before" && "$copart_configuration_after" == "$copart_configuration_before" && "$copart_identity_after" == "$copart_identity_before" ]] || fail "Copart Excel job changed unexpectedly"
[[ "$copart_auto_args_after" == "$copart_auto_args_before" && "$copart_auto_template_after" == "$copart_auto_template_before" && "$copart_auto_configuration_after" == "$copart_auto_configuration_before" && "$copart_auto_identity_after" == "$copart_auto_identity_before" ]] || fail "Scheduled Copart auto job changed unexpectedly"
[[ "$generic_trigger_after" == "$generic_trigger_before" && "$generic_template_after" == "$generic_template_before" && "$generic_configuration_after" == "$generic_configuration_before" && "$generic_identity_after" == "$generic_identity_before" ]] || fail "Generic job changed unexpectedly"

curl --fail --silent --show-error --retry 12 --retry-delay 5 "https://${api_fqdn}/healthz" >/dev/null || fail "New API revision did not pass healthz"
printf 'API_R25_DEPLOY_COMPLETED\nAPI_IMAGE=%s\nAPI_REVISION=%s\nAPI_READY_REVISION=%s\nAPI_STATE=%s\n' "$api_image_after" "$api_revision_after" "$api_ready_after" "$api_state_after"
printf 'IAAI_JOB_CHANGED=false\nCOPART_JOB_CHANGED=false\nCOPART_AUTO_JOB_CHANGED=false\nGENERIC_JOB_CHANGED=false\nCOPART_APIBARA_ENABLED=false\nMIGRATIONS_ENABLED=false\nPROJECTION_REBUILD_STARTED=false\n'
