#!/usr/bin/env bash
# Enables additive schema migrations only on the read API. It does not build an
# image, start a job, alter the IAAI job template, or modify any schedule.
set -Eeuo pipefail

readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly EXPECTED_CRON="15,45 * * * *"
readonly EXPECTED_JOB_IMAGE_SUFFIX=":execution-audit-r6b-nullsafe"

fail() { printf 'AUDIT_MIGRATION_DEPLOY_ERROR: %s\n' "$*" >&2; exit 1; }

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
az extension add --name containerapp --upgrade --only-show-errors

job_image_before="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query 'properties.template.containers[0].image' --output tsv)"
trigger_before="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.triggerType --output tsv)"
cron_before="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.scheduleTriggerConfig.cronExpression --output tsv)"

[[ "$trigger_before" == "Schedule" ]] || fail "Expected IAAI Schedule trigger; found: ${trigger_before:-empty}"
[[ "$cron_before" == "$EXPECTED_CRON" ]] || fail "Expected IAAI cron ${EXPECTED_CRON}; found: ${cron_before:-empty}"
[[ "$job_image_before" == *"$EXPECTED_JOB_IMAGE_SUFFIX" ]] || fail "Expected IAAI r6b image; found: ${job_image_before:-empty}"

printf 'AUDIT_MIGRATION_API_UPDATE_START\n'
az containerapp update --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --set-env-vars Persistence__RunMigrations=true --output none

job_image_after="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query 'properties.template.containers[0].image' --output tsv)"
trigger_after="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.triggerType --output tsv)"
cron_after="$(az containerapp job show --name "$IAAI_JOB_NAME" --resource-group "$RESOURCE_GROUP" --query properties.configuration.scheduleTriggerConfig.cronExpression --output tsv)"
api_migrations="$(az containerapp show --name "$API_NAME" --resource-group "$RESOURCE_GROUP" --query "properties.template.containers[0].env[?name=='Persistence__RunMigrations'].value | [0]" --output tsv)"

[[ "$trigger_after" == "$trigger_before" && "$cron_after" == "$cron_before" && "$job_image_after" == "$job_image_before" ]] || fail "IAAI job changed unexpectedly"
[[ "$api_migrations" == "true" ]] || fail "API migration flag was not set"

printf 'AUDIT_MIGRATION_DEPLOY_COMPLETED\n'
printf 'IAAI_JOB_EXECUTION_STARTED=false\n'
printf 'IAAI_JOB_IMAGE=%s\n' "$job_image_after"
printf 'TRIGGER=%s\n' "$trigger_after"
printf 'CRON=%s\n' "$cron_after"
printf 'PERSISTENCE_RUN_MIGRATIONS=%s\n' "$api_migrations"
printf 'COPART_APIBARA_ENABLED=false\n'
