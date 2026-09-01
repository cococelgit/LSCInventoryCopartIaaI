#!/usr/bin/env bash
# Deploys the latency-focused API/job revision after the r10 projection is ready.
# Keeps one API replica warm; never starts the IAAI job or changes its schedule.
set -Eeuo pipefail

readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly REGISTRY_NAME="acrlscinvprodeus2"
readonly REGISTRY_LOGIN_SERVER="acrlscinvprodeus2.azurecr.io"
readonly IMAGE_REPOSITORY="lsc-inventory-engine"
readonly IMAGE_TAG="inventory-search-r11-performance"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly EXPECTED_CRON="15,45 * * * *"
readonly SOURCE_CONTEXT_URL="https://lsc-inv-revi-zyn4tlbw.manus.space/manus-storage/lsc-inventory-engine-search-r11-performance.tar_88535684.gz"
readonly SOURCE_CONTEXT_SHA256="4726d02cae0756577eae5a6d72ba6ae2c7e418ed1db6539fbb7083ee0660ac72"

fail() { printf 'SEARCH_R11_DEPLOY_ERROR: %s\n' "$*" >&2; exit 1; }
fingerprint_job_field() {
  local digest
  az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query "$1" --output json \
    | sha256sum \
    | { read -r digest _; printf '%s\n' "$digest"; }
}

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
az extension add --name containerapp --upgrade --only-show-errors

job_image_before="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query 'properties.template.containers[0].image' --output tsv)"
trigger_before="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.triggerType --output tsv)"
cron_before="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.scheduleTriggerConfig.cronExpression --output tsv)"
identity_before="$(fingerprint_job_field identity)"
command_before="$(fingerprint_job_field 'properties.template.containers[0].command')"
environment_before="$(fingerprint_job_field 'properties.template.containers[0].env')"

[[ "$trigger_before" == "Schedule" ]] || fail "Expected IAAI Schedule trigger; found: ${trigger_before:-empty}"
[[ "$cron_before" == "$EXPECTED_CRON" ]] || fail "Expected IAAI cron ${EXPECTED_CRON}; found: ${cron_before:-empty}"
[[ "$job_image_before" == *":inventory-search-r10-projection" ]] || fail "Expected IAAI r10 image; found: ${job_image_before:-empty}"

image_ref="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:${IMAGE_TAG}"
printf 'SEARCH_R11_BUILD_START image=%s source_sha256=%s\n' "$image_ref" "$SOURCE_CONTEXT_SHA256"
az acr build --registry "$REGISTRY_NAME" --image "${IMAGE_REPOSITORY}:${IMAGE_TAG}" --file Dockerfile "$SOURCE_CONTEXT_URL"

printf 'SEARCH_R11_API_UPDATE_START\n'
az containerapp update --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --image "$image_ref" \
  --min-replicas 1 --set-env-vars Persistence__RunMigrations=false SearchProjection__WarmupOnStartup=true --output none
printf 'SEARCH_R11_JOB_IMAGE_UPDATE_START\n'
az containerapp job update --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --image "$image_ref" --output none

trigger_after="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.triggerType --output tsv)"
cron_after="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.scheduleTriggerConfig.cronExpression --output tsv)"
job_image_after="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query 'properties.template.containers[0].image' --output tsv)"
identity_after="$(fingerprint_job_field identity)"
command_after="$(fingerprint_job_field 'properties.template.containers[0].command')"
environment_after="$(fingerprint_job_field 'properties.template.containers[0].env')"
api_revision="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.latestRevisionName --output tsv)"
api_state="$(az containerapp revision list --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "[?name=='${api_revision}'].properties.runningState | [0]" --output tsv)"
api_min_replicas="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query 'properties.template.scale.minReplicas' --output tsv)"

[[ "$trigger_after" == "Schedule" && "$cron_after" == "$EXPECTED_CRON" ]] || fail "Job Schedule or cron changed unexpectedly"
[[ "$job_image_after" == "$image_ref" ]] || fail "IAAI job image was not updated to r11"
[[ "$identity_after" == "$identity_before" ]] || fail "IAAI job identity changed unexpectedly"
[[ "$command_after" == "$command_before" ]] || fail "IAAI job command changed unexpectedly"
[[ "$environment_after" == "$environment_before" ]] || fail "IAAI job environment changed unexpectedly"
[[ "$api_min_replicas" == "1" ]] || fail "API min replicas is not 1"

printf 'SEARCH_R11_DEPLOY_COMPLETED\n'
printf 'API_IMAGE=%s\nAPI_REVISION=%s\nAPI_STATE=%s\nAPI_MIN_REPLICAS=%s\n' "$image_ref" "$api_revision" "$api_state" "$api_min_replicas"
printf 'IAAI_JOB_IMAGE=%s\nTRIGGER=%s\nCRON=%s\n' "$job_image_after" "$trigger_after" "$cron_after"
printf 'IAAI_JOB_EXECUTION_STARTED=false\nCOPART_APIBARA_ENABLED=false\n'
