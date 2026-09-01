#!/usr/bin/env bash
# Builds the audited Engine revision and updates the read API plus the existing
# IAAI job image. It never starts a job and verifies Schedule, cron, identity,
# command and environment are unchanged.
set -Eeuo pipefail

readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly REGISTRY_NAME="acrlscinvprodeus2"
readonly REGISTRY_LOGIN_SERVER="acrlscinvprodeus2.azurecr.io"
readonly IMAGE_REPOSITORY="lsc-inventory-engine"
readonly IMAGE_TAG="execution-audit-r8-owned-schema"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly EXPECTED_CRON="15,45 * * * *"
readonly SOURCE_CONTEXT_URL="https://lsc-inv-revi-zyn4tlbw.manus.space/manus-storage/lsc-inventory-engine-execution-audit-r8.tar_75a98e81.gz"
readonly SOURCE_CONTEXT_SHA256="fec563dcb4f89ed3ac84ea5253418c567c2cbea0228a4e21d5cd4fcf38934b00"

fail() { printf 'EXECUTION_AUDIT_DEPLOY_ERROR: %s\n' "$*" >&2; exit 1; }
fingerprint_job_field() {
  local digest
  az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query "$1" --output json \
    | sha256sum \
    | { read -r digest _; printf '%s\n' "$digest"; }
}

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
az extension add --name containerapp --upgrade --only-show-errors
az acr show --name "$REGISTRY_NAME" --resource-group "$RESOURCE_GROUP" --output none
az containerapp show --name "$API_NAME" --resource-group "$RESOURCE_GROUP" --output none

job_image_before="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query 'properties.template.containers[0].image' --output tsv)"
trigger_before="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.triggerType --output tsv)"
cron_before="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.scheduleTriggerConfig.cronExpression --output tsv)"
identity_before="$(fingerprint_job_field identity)"
command_before="$(fingerprint_job_field 'properties.template.containers[0].command')"
environment_before="$(fingerprint_job_field 'properties.template.containers[0].env')"

[[ "$trigger_before" == "Schedule" ]] || fail "Expected IAAI Schedule trigger; found: ${trigger_before:-empty}"
[[ "$cron_before" == "$EXPECTED_CRON" ]] || fail "Expected IAAI cron ${EXPECTED_CRON}; found: ${cron_before:-empty}"
[[ "$job_image_before" == *":execution-audit-r6b-nullsafe" ]] || fail "Expected audited IAAI r6b image; found: ${job_image_before:-empty}"

image_ref="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:${IMAGE_TAG}"
printf 'EXECUTION_AUDIT_BUILD_START image=%s source_sha256=%s\n' "$image_ref" "$SOURCE_CONTEXT_SHA256"
az acr build --registry "$REGISTRY_NAME" --image "${IMAGE_REPOSITORY}:${IMAGE_TAG}" --file Dockerfile "$SOURCE_CONTEXT_URL"

printf 'EXECUTION_AUDIT_API_UPDATE_START\n'
az containerapp update --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --image "$image_ref" --output none
printf 'EXECUTION_AUDIT_JOB_IMAGE_UPDATE_START\n'
az containerapp job update --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --image "$image_ref" --output none

trigger_after="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.triggerType --output tsv)"
cron_after="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.scheduleTriggerConfig.cronExpression --output tsv)"
job_image_after="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query 'properties.template.containers[0].image' --output tsv)"
identity_after="$(fingerprint_job_field identity)"
command_after="$(fingerprint_job_field 'properties.template.containers[0].command')"
environment_after="$(fingerprint_job_field 'properties.template.containers[0].env')"
api_revision="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.latestRevisionName --output tsv)"
api_state="$(az containerapp revision list --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "[?name=='${api_revision}'].properties.runningState | [0]" --output tsv)"

[[ "$trigger_after" == "Schedule" && "$cron_after" == "$EXPECTED_CRON" ]] || fail "Job Schedule or cron changed unexpectedly"
[[ "$job_image_after" == "$image_ref" ]] || fail "IAAI job image was not updated to the audited release"
[[ "$identity_after" == "$identity_before" ]] || fail "IAAI job identity changed unexpectedly"
[[ "$command_after" == "$command_before" ]] || fail "IAAI job command changed unexpectedly"
[[ "$environment_after" == "$environment_before" ]] || fail "IAAI job environment changed unexpectedly"

printf 'EXECUTION_AUDIT_DEPLOY_COMPLETED\n'
printf 'API_IMAGE=%s\n' "$image_ref"
printf 'API_REVISION=%s\n' "$api_revision"
printf 'API_STATE=%s\n' "$api_state"
printf 'IAAI_JOB_IMAGE=%s\n' "$job_image_after"
printf 'TRIGGER=%s\n' "$trigger_after"
printf 'CRON=%s\n' "$cron_after"
printf 'IAAI_JOB_EXECUTION_STARTED=false\n'
printf 'COPART_APIBARA_ENABLED=false\n'
