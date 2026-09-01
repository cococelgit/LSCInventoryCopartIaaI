#!/usr/bin/env bash
# Builds a verified Engine image and updates only the read API. It deliberately
# does not update, schedule, or execute the IAAI job; Copart remains Excel-only.
set -Eeuo pipefail

readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly REGISTRY_NAME="acrlscinvprodeus2"
readonly REGISTRY_LOGIN_SERVER="acrlscinvprodeus2.azurecr.io"
readonly IMAGE_REPOSITORY="lsc-inventory-engine"
readonly IMAGE_TAG="inventory-search-r5d-title-exclusion"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly SOURCE_CONTEXT_URL="https://lsc-inv-revi-zyn4tlbw.manus.space/manus-storage/lsc-inventory-engine-r5d-title-exclusion.tar_71dc2f0d.gz"
readonly SOURCE_CONTEXT_SHA256="ab69c7864372f2027c9736b8729fa57a49a8eac3630c67cdc1fb882c2482f1af"

fail() { printf 'INVENTORY_SEARCH_API_DEPLOY_ERROR: %s\n' "$*" >&2; exit 1; }

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
az extension add --name containerapp --upgrade --only-show-errors
az acr show --name "$REGISTRY_NAME" --resource-group "$RESOURCE_GROUP" --output none
az containerapp show --name "$API_NAME" --resource-group "$RESOURCE_GROUP" --output none
job_image_before="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query 'properties.template.containers[0].image' --output tsv)"
job_trigger_before="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.triggerType --output tsv)"

image_ref="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:${IMAGE_TAG}"
printf 'INVENTORY_SEARCH_API_BUILD_START image=%s source_sha256=%s\n' "$image_ref" "$SOURCE_CONTEXT_SHA256"
az acr build --registry "$REGISTRY_NAME" --image "${IMAGE_REPOSITORY}:${IMAGE_TAG}" --file Dockerfile "$SOURCE_CONTEXT_URL"

printf 'INVENTORY_SEARCH_API_UPDATE_START\n'
az containerapp update --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --image "$image_ref" --output none

api_revision="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.latestRevisionName --output tsv)"
api_state="$(az containerapp revision list --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "[?name=='${api_revision}'].properties.runningState | [0]" --output tsv)"
job_image_after="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query 'properties.template.containers[0].image' --output tsv)"
job_trigger_after="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.triggerType --output tsv)"

[[ "$job_image_after" == "$job_image_before" ]] || fail "IAAI job image changed unexpectedly"
[[ "$job_trigger_after" == "$job_trigger_before" ]] || fail "IAAI job trigger changed unexpectedly"
printf 'INVENTORY_SEARCH_API_DEPLOY_COMPLETED\n'
printf 'API_IMAGE=%s\n' "$image_ref"
printf 'API_REVISION=%s\n' "$api_revision"
printf 'API_STATE=%s\n' "$api_state"
printf 'IAAI_JOB_IMAGE_UNCHANGED=%s\n' "$job_image_after"
printf 'IAAI_JOB_TRIGGER_UNCHANGED=%s\n' "$job_trigger_after"
printf 'IAAI_JOB_EXECUTION_STARTED=false\n'
printf 'SCHEDULE_ENABLED=false\n'
printf 'COPART_APIBARA_ENABLED=false\n'
