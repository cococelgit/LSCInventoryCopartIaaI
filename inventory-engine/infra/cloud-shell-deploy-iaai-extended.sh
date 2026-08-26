#!/usr/bin/env bash
# Builds the fixed IAAI extended release package and updates only the read API
# and the manual IAAI job. It deliberately does not run either job.
set -Eeuo pipefail

readonly TENANT_ID="ccfdc482-7c38-458c-b7b7-b7967a122f1d"
readonly SUBSCRIPTION_NAME="LSC Inventory Feed Project"
readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly REGISTRY_NAME="acrlscinvprodeus2"
readonly REGISTRY_LOGIN_SERVER="acrlscinvprodeus2.azurecr.io"
readonly IMAGE_REPOSITORY="lsc-inventory"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly RELEASE_ID="iaai-extended-20260826r1"
readonly IMAGE_TAG="iaai-extended-20260826r1"
readonly RELEASE_MANIFEST="inventory-engine/infra/RELEASE_IAAI_EXTENDED_20260826.txt"

fail() {
  printf 'ERROR: %s\n' "$*" >&2
  exit 1
}

for command in az grep; do
  command -v "$command" >/dev/null 2>&1 || fail "Required command not found: $command"
done

account_tenant="$(az account show --query tenantId --output tsv)"
account_subscription="$(az account show --query name --output tsv)"

[[ "$account_tenant" == "$TENANT_ID" ]] || fail "Cloud Shell is in an unexpected Azure tenant. No resources were changed."
[[ "$account_subscription" == "$SUBSCRIPTION_NAME" ]] || fail "Cloud Shell is using an unexpected subscription. No resources were changed."

[[ -f "$RELEASE_MANIFEST" ]] || fail "Release manifest is absent. No Azure changes were made."
grep -Fx "release_id=${RELEASE_ID}" "$RELEASE_MANIFEST" >/dev/null \
  || fail "Unexpected release package. No Azure changes were made."
grep -Fx "scope=build_image_update_read_api_update_manual_iaai_job_only" "$RELEASE_MANIFEST" >/dev/null \
  || fail "Release scope is invalid. No Azure changes were made."
[[ -f "inventory-engine/src/Lsc.Inventory.Api/Sources/InventorySourcePolicy.cs" ]] \
  || fail "Source-policy guard is absent. No Azure changes were made."

az acr show --name "$REGISTRY_NAME" --resource-group "$RESOURCE_GROUP" --output none
az containerapp show --name "$API_NAME" --resource-group "$RESOURCE_GROUP" --output none
az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --output none

image_ref="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:${IMAGE_TAG}"

printf 'Building verified image tag: %s\n' "$image_ref"
pushd inventory-engine >/dev/null
az acr build \
  --registry "$REGISTRY_NAME" \
  --image "${IMAGE_REPOSITORY}:${image_tag}" \
  --file Dockerfile \
  .
popd >/dev/null

printf 'Updating read API image only. Existing identity, Key Vault references, network settings and revisions remain managed by Azure.\n'
az containerapp update \
  --resource-group "$RESOURCE_GROUP" \
  --name "$API_NAME" \
  --image "$image_ref" \
  --output none

printf 'Updating manual IAAI job image only. No execution or schedule is started.\n'
az containerapp job update \
  --resource-group "$RESOURCE_GROUP" \
  --name "$IAAI_JOB_NAME" \
  --image "$image_ref" \
  --output none

api_revision="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.latestRevisionName --output tsv)"
api_state="$(az containerapp revision list --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "[?name=='${api_revision}'].properties.runningState | [0]" --output tsv)"
job_image="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query 'properties.template.containers[0].image' --output tsv)"

printf '\nDEPLOYMENT_COMPLETED\n'
printf 'Release: %s\n' "$RELEASE_ID"
printf 'Image: %s\n' "$image_ref"
printf 'API revision: %s\n' "$api_revision"
printf 'API state: %s\n' "$api_state"
printf 'Manual IAAI job image: %s\n' "$job_image"
printf 'No IAAI execution was started. Copart remains blocked from Apibara by the deployed code path.\n'
