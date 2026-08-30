#!/usr/bin/env bash
set -euo pipefail

SUBSCRIPTION_ID="99edf3d1-a651-4d2d-af8e-d44a3360f584"
RESOURCE_GROUP="rg-lsc-inventory-prod"
API_NAME="ca-lsc-inventory-api-prod"
GENERIC_JOB_NAME="job-lsc-inventory-ingestion-prod"
NATIONAL_JOB_NAME="job-lsc-iaai-pilot-prod"
EXPECTED_API_IMAGE="acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:inventory-api-integrated-r16"
EXPECTED_NATIONAL_IMAGE="acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:iaai-cursor-recovery-r14"

az account set --subscription "$SUBSCRIPTION_ID"

api_before="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query '{image:properties.template.containers[0].image,revision:properties.latestRevisionName,identity:identity.type,ingress:properties.configuration.ingress}' --output json)"
generic_before="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$GENERIC_JOB_NAME" --query '{image:properties.template.containers[0].image,command:properties.template.containers[0].command,args:properties.template.containers[0].args,trigger:properties.configuration.triggerType,identity:identity.type,secrets:properties.configuration.secrets,env:properties.template.containers[0].env,timeout:properties.configuration.replicaTimeout,retries:properties.configuration.replicaRetryLimit}' --output json)"
national_before="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$NATIONAL_JOB_NAME" --query '{image:properties.template.containers[0].image,trigger:properties.configuration.triggerType,cron:properties.configuration.scheduleTriggerConfig.cronExpression,identity:identity.type,secrets:properties.configuration.secrets,env:properties.template.containers[0].env,timeout:properties.configuration.replicaTimeout,retries:properties.configuration.replicaRetryLimit}' --output json)"

api_image="$(printf '%s' "$api_before" | jq -r '.image // empty')"
generic_trigger="$(printf '%s' "$generic_before" | jq -r '.trigger // empty')"
generic_command="$(printf '%s' "$generic_before" | jq -c '{command,args}')"
national_image="$(printf '%s' "$national_before" | jq -r '.image // empty')"
national_trigger="$(printf '%s' "$national_before" | jq -r '.trigger // empty')"
national_cron="$(printf '%s' "$national_before" | jq -r '.cron // empty')"

if [[ "$api_image" != "$EXPECTED_API_IMAGE" ]]; then
  echo "R17_ABORT: API image differs from integrated r16; found $api_image" >&2
  exit 41
fi
if [[ "$generic_trigger" != "Schedule" ]]; then
  echo "R17_ABORT: generic job must be Schedule before disabling it; found $generic_trigger" >&2
  exit 42
fi
if [[ "$generic_command" != *"--run-once"* ]]; then
  echo "R17_ABORT: generic job no longer has expected --run-once command" >&2
  exit 43
fi
if [[ "$national_image" != "$EXPECTED_NATIONAL_IMAGE" || "$national_trigger" != "Schedule" || "$national_cron" != "15,45 * * * *" ]]; then
  echo "R17_ABORT: national IAAI job guardrail failed" >&2
  exit 44
fi

generic_guard_before="$(printf '%s' "$generic_before" | jq -S 'del(.trigger)')"
api_guard_before="$(printf '%s' "$api_before" | jq -S .)"
national_guard_before="$(printf '%s' "$national_before" | jq -S .)"

az containerapp job update --resource-group "$RESOURCE_GROUP" --name "$GENERIC_JOB_NAME" --trigger-type Manual --output none

api_after="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query '{image:properties.template.containers[0].image,revision:properties.latestRevisionName,identity:identity.type,ingress:properties.configuration.ingress}' --output json)"
generic_after="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$GENERIC_JOB_NAME" --query '{image:properties.template.containers[0].image,command:properties.template.containers[0].command,args:properties.template.containers[0].args,trigger:properties.configuration.triggerType,identity:identity.type,secrets:properties.configuration.secrets,env:properties.template.containers[0].env,timeout:properties.configuration.replicaTimeout,retries:properties.configuration.replicaRetryLimit}' --output json)"
national_after="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$NATIONAL_JOB_NAME" --query '{image:properties.template.containers[0].image,trigger:properties.configuration.triggerType,cron:properties.configuration.scheduleTriggerConfig.cronExpression,identity:identity.type,secrets:properties.configuration.secrets,env:properties.template.containers[0].env,timeout:properties.configuration.replicaTimeout,retries:properties.configuration.replicaRetryLimit}' --output json)"

generic_guard_after="$(printf '%s' "$generic_after" | jq -S 'del(.trigger)')"
api_guard_after="$(printf '%s' "$api_after" | jq -S .)"
national_guard_after="$(printf '%s' "$national_after" | jq -S .)"
generic_trigger_after="$(printf '%s' "$generic_after" | jq -r '.trigger // empty')"

if [[ "$generic_trigger_after" != "Manual" ]]; then
  echo "R17_DEPLOY_ERROR: generic job trigger did not change to Manual" >&2
  exit 45
fi
if [[ "$generic_guard_before" != "$generic_guard_after" ]]; then
  echo "R17_DEPLOY_ERROR: generic job changed outside triggerType" >&2
  exit 46
fi
if [[ "$api_guard_before" != "$api_guard_after" ]]; then
  echo "R17_DEPLOY_ERROR: API changed during generic job update" >&2
  exit 47
fi
if [[ "$national_guard_before" != "$national_guard_after" ]]; then
  echo "R17_DEPLOY_ERROR: national IAAI job changed during generic job update" >&2
  exit 48
fi

echo "api_changed=false"
echo "generic_job_trigger=Manual"
echo "generic_job_execution_started=false"
echo "national_job_changed=false"
echo "copart_apibara_enabled=false"
echo "migrations_enabled=false"
