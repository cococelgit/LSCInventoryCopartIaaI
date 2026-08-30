#!/usr/bin/env bash
# Controlled national IAAI transition. This builds a verified source package,
# updates the read API and converts only the existing manual IAAI job command
# to national mode. It never starts an execution, changes a secret, or enables
# a schedule. The schedule is a separate, explicitly approved operation.
set -Eeuo pipefail

readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly REGISTRY_NAME="acrlscinvprodeus2"
readonly REGISTRY_LOGIN_SERVER="acrlscinvprodeus2.azurecr.io"
readonly IMAGE_REPOSITORY="lsc-inventory-engine"
readonly IMAGE_TAG="iaai-national-r4-diagnostic"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly SOURCE_CONTEXT_URL="https://lsc-inv-revi-zyn4tlbw.manus.space/manus-storage/lsc-inventory-engine-iaai-national-r4-diagnostic.tar_8c6714e2.gz"
readonly SOURCE_CONTEXT_SHA256="63253a4253b7f977a30a8b5d6dd8e889621b3d92b0789667b6ee75bd494dc0e6"

fail() {
  printf 'NATIONAL_TRANSITION_ERROR: %s\n' "$*" >&2
  exit 1
}

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
az extension add --name containerapp --upgrade --only-show-errors
az acr show --name "$REGISTRY_NAME" --resource-group "$RESOURCE_GROUP" --output none
az containerapp show --name "$API_NAME" --resource-group "$RESOURCE_GROUP" --output none
current_trigger="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.triggerType --output tsv)"
[[ "$current_trigger" == "Manual" ]] || fail "Expected manual IAAI job before transition; found: ${current_trigger:-empty}"

image_ref="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:${IMAGE_TAG}"
printf 'NATIONAL_BUILD_START image=%s source_sha256=%s\n' "$image_ref" "$SOURCE_CONTEXT_SHA256"
az acr build \
  --registry "$REGISTRY_NAME" \
  --image "${IMAGE_REPOSITORY}:${IMAGE_TAG}" \
  --file Dockerfile \
  "$SOURCE_CONTEXT_URL"

printf 'NATIONAL_API_UPDATE_START\n'
az containerapp update \
  --resource-group "$RESOURCE_GROUP" \
  --name "$API_NAME" \
  --image "$image_ref" \
  --output none

printf 'NATIONAL_MANUAL_JOB_UPDATE_START\n'
az containerapp job update \
  --resource-group "$RESOURCE_GROUP" \
  --name "$IAAI_JOB_NAME" \
  --image "$image_ref" \
  --command dotnet \
  --args Lsc.Inventory.Api.dll \
  --set-env-vars \
    Sync__Enabled=false \
    IaaINational__Enabled=true \
    IaaINational__RunOnStartup=true \
    IaaINational__LotSubStatus=Open \
    IaaINational__PagesPerRun=3 \
    IaaINational__MaxRequestsPerRun=15 \
    IaaINational__BackfillPagesPerRun=3200 \
    IaaINational__BackfillMaxRequestsPerRun=3200 \
    IaaINational__MaintenancePagesPerRun=3 \
    IaaINational__MaintenanceMaxRequestsPerRun=15 \
    IaaINational__EnrichVehicleDetails=true \
    IaaINational__BackfillDetailEnrichmentLimitPerRun=20 \
    IaaINational__MaintenanceDetailEnrichmentLimitPerRun=6 \
    IaaINational__LeaseMinutes=240 \
    IaaINational__CaptureUsage=true \
    IaaINational__MinimumRemainingRequests=2000 \
  --replica-timeout 7200 \
  --output none

api_revision="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.latestRevisionName --output tsv)"
api_state="$(az containerapp revision list --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "[?name=='${api_revision}'].properties.runningState | [0]" --output tsv)"
job_image="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query 'properties.template.containers[0].image' --output tsv)"
job_command="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query 'properties.template.containers[0].command' --output tsv)"
job_trigger="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query properties.configuration.triggerType --output tsv)"

[[ "$job_trigger" == "Manual" ]] || fail "Schedule was unexpectedly changed: ${job_trigger:-empty}"
printf 'NATIONAL_TRANSITION_COMPLETED\n'
printf 'IMAGE=%s\n' "$image_ref"
printf 'API_REVISION=%s\n' "$api_revision"
printf 'API_STATE=%s\n' "$api_state"
printf 'IAAI_JOB_IMAGE=%s\n' "$job_image"
printf 'IAAI_JOB_COMMAND=%s\n' "$job_command"
printf 'IAAI_JOB_TRIGGER=%s\n' "$job_trigger"
printf 'IAAI_JOB_EXECUTION_STARTED=false\n'
printf 'SCHEDULE_ENABLED=false\n'
printf 'COPART_APIBARA_ENABLED=false\n'
