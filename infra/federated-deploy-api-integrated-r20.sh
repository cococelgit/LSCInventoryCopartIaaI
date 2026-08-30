#!/usr/bin/env bash
# Builds the next integrated Inventory API release and updates only the shared API Container App.
set -Eeuo pipefail

readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly REGISTRY_NAME="acrlscinvprodeus2"
readonly REGISTRY_LOGIN_SERVER="acrlscinvprodeus2.azurecr.io"
readonly IMAGE_REPOSITORY="lsc-inventory-engine"
readonly IMAGE_TAG="inventory-api-integrated-r20-active-summary"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly COPART_JOB_NAME="job-lsc-copart-excel-prod"
readonly GENERIC_JOB_NAME="job-lsc-inventory-ingestion-prod"
readonly EXPECTED_API_IMAGE="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:inventory-api-integrated-r19-active-inventory"
readonly EXPECTED_IAAI_IMAGE="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:iaai-cursor-recovery-r14"
readonly EXPECTED_IAAI_CRON="15,45 * * * *"
readonly SOURCE_CONTEXT_URL="https://lsc-inv-revi-zyn4tlbw.manus.space/manus-storage/lsc-inventory-engine-integrated-api-r20-summary.tar_351e8a2d.gz"
readonly SOURCE_CONTEXT_SHA256="25d240baffdf3c2624fd8534d0f3994479820e03fb7577f1bfdf5ee7467eb44c"

fail() { printf 'API_R20_DEPLOY_ERROR: %s\n' "$*" >&2; exit 1; }
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

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
command -v curl >/dev/null 2>&1 || fail "curl is unavailable"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is unavailable"
az extension add --name containerapp --upgrade --only-show-errors

api_image_before="$(app_field 'properties.template.containers[0].image')"
api_mode_before="$(app_field properties.configuration.activeRevisionsMode)"
api_identity_before="$(fingerprint_app identity)"
api_secrets_before="$(fingerprint_app properties.configuration.secrets)"
api_ingress_before="$(fingerprint_app properties.configuration.ingress)"

iaai_image_before="$(job_field "$IAAI_JOB_NAME" 'properties.template.containers[0].image')"
iaai_trigger_before="$(job_field "$IAAI_JOB_NAME" properties.configuration.triggerType)"
iaai_cron_before="$(job_field "$IAAI_JOB_NAME" properties.configuration.scheduleTriggerConfig.cronExpression)"
iaai_template_before="$(fingerprint_job "$IAAI_JOB_NAME" properties.template)"
iaai_identity_before="$(fingerprint_job "$IAAI_JOB_NAME" identity)"
iaai_secrets_before="$(fingerprint_job "$IAAI_JOB_NAME" properties.configuration.secrets)"

copart_template_before="$(fingerprint_job "$COPART_JOB_NAME" properties.template)"
copart_configuration_before="$(fingerprint_job "$COPART_JOB_NAME" properties.configuration)"
copart_identity_before="$(fingerprint_job "$COPART_JOB_NAME" identity)"

generic_trigger_before="$(job_field "$GENERIC_JOB_NAME" properties.configuration.triggerType)"
generic_template_before="$(fingerprint_job "$GENERIC_JOB_NAME" properties.template)"
generic_configuration_before="$(fingerprint_job "$GENERIC_JOB_NAME" properties.configuration)"
generic_identity_before="$(fingerprint_job "$GENERIC_JOB_NAME" identity)"

[[ "$api_image_before" == "$EXPECTED_API_IMAGE" ]] || fail "Expected integrated r19 API image; found: ${api_image_before:-empty}"
[[ "$api_mode_before" == "Single" ]] || fail "Expected API Single revision mode; found: ${api_mode_before:-empty}"
[[ "$iaai_image_before" == "$EXPECTED_IAAI_IMAGE" ]] || fail "Expected IAAI r14 image; found: ${iaai_image_before:-empty}"
[[ "$iaai_trigger_before" == "Schedule" ]] || fail "Expected IAAI Schedule trigger; found: ${iaai_trigger_before:-empty}"
[[ "$iaai_cron_before" == "$EXPECTED_IAAI_CRON" ]] || fail "Expected IAAI cron ${EXPECTED_IAAI_CRON}; found: ${iaai_cron_before:-empty}"
[[ "$generic_trigger_before" == "Manual" ]] || fail "Expected generic IAAI FL job to remain Manual; found: ${generic_trigger_before:-empty}"

temporary_source="$(mktemp)"
trap 'rm -f "$temporary_source"' EXIT
curl --fail --silent --show-error --location --retry 3 --retry-delay 2 "$SOURCE_CONTEXT_URL" --output "$temporary_source"
read -r source_hash _ < <(sha256sum "$temporary_source")
[[ "$source_hash" == "$SOURCE_CONTEXT_SHA256" ]] || fail "Integrated source checksum mismatch"

image_ref="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:${IMAGE_TAG}"
printf 'API_R20_BUILD_START image=%s source_sha256=%s\n' "$image_ref" "$SOURCE_CONTEXT_SHA256"
az acr build --registry "$REGISTRY_NAME" --image "${IMAGE_REPOSITORY}:${IMAGE_TAG}" --file Dockerfile "$SOURCE_CONTEXT_URL"

printf 'API_R20_UPDATE_START\n'
az containerapp update \
  --resource-group "$RESOURCE_GROUP" \
  --name "$API_NAME" \
  --image "$image_ref" \
  --min-replicas 1 \
  --set-env-vars \
    Sync__Enabled=false \
    Persistence__RunMigrations=false \
    SearchProjection__WarmupOnStartup=true \
  --output none

api_image_after="$(app_field 'properties.template.containers[0].image')"
api_revision_after="$(app_field properties.latestRevisionName)"
api_ready_after="$(app_field properties.latestReadyRevisionName)"
api_mode_after="$(app_field properties.configuration.activeRevisionsMode)"
api_min_replicas_after="$(app_field properties.template.scale.minReplicas)"
api_identity_after="$(fingerprint_app identity)"
api_secrets_after="$(fingerprint_app properties.configuration.secrets)"
api_ingress_after="$(fingerprint_app properties.configuration.ingress)"
api_state_after="$(az containerapp revision list --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "[?name=='${api_revision_after}'].properties.runningState | [0]" --output tsv)"

iaai_image_after="$(job_field "$IAAI_JOB_NAME" 'properties.template.containers[0].image')"
iaai_trigger_after="$(job_field "$IAAI_JOB_NAME" properties.configuration.triggerType)"
iaai_cron_after="$(job_field "$IAAI_JOB_NAME" properties.configuration.scheduleTriggerConfig.cronExpression)"
iaai_template_after="$(fingerprint_job "$IAAI_JOB_NAME" properties.template)"
iaai_identity_after="$(fingerprint_job "$IAAI_JOB_NAME" identity)"
iaai_secrets_after="$(fingerprint_job "$IAAI_JOB_NAME" properties.configuration.secrets)"

copart_template_after="$(fingerprint_job "$COPART_JOB_NAME" properties.template)"
copart_configuration_after="$(fingerprint_job "$COPART_JOB_NAME" properties.configuration)"
copart_identity_after="$(fingerprint_job "$COPART_JOB_NAME" identity)"

generic_trigger_after="$(job_field "$GENERIC_JOB_NAME" properties.configuration.triggerType)"
generic_template_after="$(fingerprint_job "$GENERIC_JOB_NAME" properties.template)"
generic_configuration_after="$(fingerprint_job "$GENERIC_JOB_NAME" properties.configuration)"
generic_identity_after="$(fingerprint_job "$GENERIC_JOB_NAME" identity)"

[[ "$api_image_after" == "$image_ref" ]] || fail "API image was not updated to r20"
[[ "$api_mode_after" == "$api_mode_before" ]] || fail "API revision mode changed unexpectedly"
[[ "$api_min_replicas_after" == "1" ]] || fail "API min replicas is not 1"
[[ "$api_identity_after" == "$api_identity_before" ]] || fail "API identity changed unexpectedly"
[[ "$api_secrets_after" == "$api_secrets_before" ]] || fail "API secrets changed unexpectedly"
[[ "$api_ingress_after" == "$api_ingress_before" ]] || fail "API ingress changed unexpectedly"

[[ "$iaai_image_after" == "$iaai_image_before" ]] || fail "IAAI image changed unexpectedly"
[[ "$iaai_trigger_after" == "$iaai_trigger_before" ]] || fail "IAAI trigger changed unexpectedly"
[[ "$iaai_cron_after" == "$iaai_cron_before" ]] || fail "IAAI cron changed unexpectedly"
[[ "$iaai_template_after" == "$iaai_template_before" ]] || fail "IAAI template changed unexpectedly"
[[ "$iaai_identity_after" == "$iaai_identity_before" ]] || fail "IAAI identity changed unexpectedly"
[[ "$iaai_secrets_after" == "$iaai_secrets_before" ]] || fail "IAAI secrets changed unexpectedly"

[[ "$copart_template_after" == "$copart_template_before" ]] || fail "Copart job template changed unexpectedly"
[[ "$copart_configuration_after" == "$copart_configuration_before" ]] || fail "Copart job configuration changed unexpectedly"
[[ "$copart_identity_after" == "$copart_identity_before" ]] || fail "Copart job identity changed unexpectedly"

[[ "$generic_trigger_after" == "$generic_trigger_before" ]] || fail "Generic job trigger changed unexpectedly"
[[ "$generic_template_after" == "$generic_template_before" ]] || fail "Generic job template changed unexpectedly"
[[ "$generic_configuration_after" == "$generic_configuration_before" ]] || fail "Generic job configuration changed unexpectedly"
[[ "$generic_identity_after" == "$generic_identity_before" ]] || fail "Generic job identity changed unexpectedly"

printf 'API_R19_DEPLOY_COMPLETED\n'
printf 'API_IMAGE=%s\nAPI_REVISION=%s\nAPI_READY_REVISION=%s\nAPI_STATE=%s\nAPI_MIN_REPLICAS=%s\n' "$api_image_after" "$api_revision_after" "$api_ready_after" "$api_state_after" "$api_min_replicas_after"
printf 'IAAI_JOB_CHANGED=false\nCOPART_JOB_CHANGED=false\nGENERIC_JOB_CHANGED=false\nCOPART_APIBARA_ENABLED=false\nMIGRATIONS_ENABLED=false\n'
