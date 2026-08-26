#!/usr/bin/env bash
# One-shot deployment invoked by an ARM deployment script using a managed identity.
# Scope: build a fixed remote-tarball source context, update the read API, update the manual IAAI
# job. It never runs a job, enables a schedule, changes provider sources, handles
# secrets, or changes Key Vault, networking, or managed identities.
set -Eeuo pipefail

readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly REGISTRY_NAME="acrlscinvprodeus2"
readonly REGISTRY_LOGIN_SERVER="acrlscinvprodeus2.azurecr.io"
readonly IMAGE_REPOSITORY="lsc-inventory-engine"
readonly IMAGE_TAG="iaai-extended-afaac3d"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
# Immutable Engine package generated from reconciled release commit
# afaac3df4e386d6fef1a421f9c34597b3a0c8240. ACR Tasks natively accepts remote tarballs.
readonly SOURCE_CONTEXT_URL="https://lsc-inv-revi-zyn4tlbw.manus.space/manus-storage/lsc-inventory-engine-iaai-extended-afaac3d.tar_4ee7b92f.gz"
readonly SOURCE_CONTEXT_SHA256="cd0b8d20a7a0cc73e4862d25378f0aded45fb3bc0bbd60378e175af707483f2f"

fail() {
  printf 'FEDERATED_DEPLOY_ERROR: %s\n' "$*" >&2
  exit 1
}

for command in az; do
  command -v "$command" >/dev/null 2>&1 || fail "Required command is unavailable: $command"
done

az extension add --name containerapp --upgrade --only-show-errors
az acr show --name "$REGISTRY_NAME" --resource-group "$RESOURCE_GROUP" --output none
az containerapp show --name "$API_NAME" --resource-group "$RESOURCE_GROUP" --output none
az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --output none

image_ref="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:${IMAGE_TAG}"
printf 'FEDERATED_BUILD_START image=%s source_commit=afaac3df4e386d6fef1a421f9c34597b3a0c8240 source_sha256=%s\n' "$image_ref" "$SOURCE_CONTEXT_SHA256"
az acr build \
  --registry "$REGISTRY_NAME" \
  --image "${IMAGE_REPOSITORY}:${IMAGE_TAG}" \
  --file Dockerfile \
  "$SOURCE_CONTEXT_URL"

printf 'FEDERATED_API_UPDATE_START\n'
az containerapp update \
  --resource-group "$RESOURCE_GROUP" \
  --name "$API_NAME" \
  --image "$image_ref" \
  --output none

printf 'FEDERATED_IAAI_JOB_UPDATE_START\n'
az containerapp job update \
  --resource-group "$RESOURCE_GROUP" \
  --name "$IAAI_JOB_NAME" \
  --image "$image_ref" \
  --output none

api_revision="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query properties.latestRevisionName --output tsv)"
api_state="$(az containerapp revision list --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "[?name=='${api_revision}'].properties.runningState | [0]" --output tsv)"
job_image="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$IAAI_JOB_NAME" --query 'properties.template.containers[0].image' --output tsv)"

printf 'FEDERATED_DEPLOYMENT_COMPLETED\n'
printf 'IMAGE=%s\n' "$image_ref"
printf 'API_REVISION=%s\n' "$api_revision"
printf 'API_STATE=%s\n' "$api_state"
printf 'IAAI_JOB_IMAGE=%s\n' "$job_image"
printf 'IAAI_JOB_EXECUTION_STARTED=false\n'
printf 'COPART_APIBARA_ENABLED=false\n'
