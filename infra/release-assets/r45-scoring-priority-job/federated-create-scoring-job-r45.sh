#!/usr/bin/env bash
# Creates or updates only the isolated LSC Pre-grade job; it never changes API, IAAI, or Copart jobs.
set -Eeuo pipefail

readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly REGISTRY_LOGIN_SERVER="acrlscinvprodeus2.azurecr.io"
readonly IMAGE_REPOSITORY="lsc-inventory-engine"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly COPART_JOB_NAME="job-lsc-copart-excel-prod"
readonly COPART_AUTO_JOB_NAME="job-lsc-copart-auto-prod"
readonly GENERIC_JOB_NAME="job-lsc-inventory-ingestion-prod"
readonly SCORING_JOB_NAME="job-lsc-inventory-scoring-prod"
readonly EXPECTED_API_IMAGE="${REGISTRY_LOGIN_SERVER}/${IMAGE_REPOSITORY}:inventory-api-integrated-r44-detail-envelope"
readonly EXPECTED_IAAI_CRON="15,45 * * * *"
readonly EXPECTED_COPART_AUTO_CRON="0,30 * * * *"
readonly SCORING_CRON="7,22,37,52 * * * *"
readonly SCORING_TIMEOUT="900"
readonly SCORING_BATCH_MAXIMUM="500"

fail() { printf 'SCORING_R45_PRIORITY_JOB_ABORT: %s\n' "$*" >&2; exit 1; }
app_field() { az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "$1" --output tsv; }
job_field() { az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$1" --query "$2" --output tsv; }
job_compact_json() { az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$1" --query "$2" --output json | tr -d '\r\n\t '; }
fingerprint_app() {
  local digest
  az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "$1" --output json | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }
}
fingerprint_job() {
  local job_name="$1" query="$2" digest
  az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$job_name" --query "$query" --output json | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }
}
source_env() { job_field "$COPART_AUTO_JOB_NAME" "properties.template.containers[0].env[?name=='$1'].value | [0]"; }
required_source_env() {
  local env_name="$1" value
  value="$(source_env "$env_name")"
  [[ -n "$value" && "$value" != "null" ]] || fail "Missing required non-secret source environment variable ${env_name}"
  printf '%s\n' "$value"
}

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is unavailable"
command -v tr >/dev/null 2>&1 || fail "tr is unavailable"
az extension add --name containerapp --upgrade --only-show-errors

api_image_before="$(app_field 'properties.template.containers[0].image')"
api_revision_before="$(app_field properties.latestRevisionName)"
api_identity_before="$(fingerprint_app identity)"
api_secrets_before="$(fingerprint_app properties.configuration.secrets)"
api_ingress_before="$(fingerprint_app properties.configuration.ingress)"

iaai_template_before="$(fingerprint_job "$IAAI_JOB_NAME" properties.template)"
iaai_configuration_before="$(fingerprint_job "$IAAI_JOB_NAME" properties.configuration)"
iaai_identity_before="$(fingerprint_job "$IAAI_JOB_NAME" identity)"
copart_template_before="$(fingerprint_job "$COPART_JOB_NAME" properties.template)"
copart_configuration_before="$(fingerprint_job "$COPART_JOB_NAME" properties.configuration)"
copart_identity_before="$(fingerprint_job "$COPART_JOB_NAME" identity)"
copart_auto_template_before="$(fingerprint_job "$COPART_AUTO_JOB_NAME" properties.template)"
copart_auto_configuration_before="$(fingerprint_job "$COPART_AUTO_JOB_NAME" properties.configuration)"
copart_auto_identity_before="$(fingerprint_job "$COPART_AUTO_JOB_NAME" identity)"
generic_template_before="$(fingerprint_job "$GENERIC_JOB_NAME" properties.template)"
generic_configuration_before="$(fingerprint_job "$GENERIC_JOB_NAME" properties.configuration)"
generic_identity_before="$(fingerprint_job "$GENERIC_JOB_NAME" identity)"

[[ "$api_image_before" == "$EXPECTED_API_IMAGE" ]] || fail "Expected API r44 image; found ${api_image_before:-empty}"
[[ "$(app_field 'properties.template.containers[0].env[?name==`Sync__Enabled`].value | [0]')" == "false" ]] || fail "API legacy sync must remain disabled"
[[ "$(job_field "$IAAI_JOB_NAME" properties.configuration.triggerType)" == "Schedule" ]] || fail "IAAI job must remain Schedule"
[[ "$(job_field "$IAAI_JOB_NAME" properties.configuration.scheduleTriggerConfig.cronExpression)" == "$EXPECTED_IAAI_CRON" ]] || fail "Unexpected IAAI cron"
[[ "$(job_field "$COPART_JOB_NAME" properties.configuration.triggerType)" == "Manual" ]] || fail "Copart Excel job must remain Manual"
[[ "$(job_field "$COPART_AUTO_JOB_NAME" properties.configuration.triggerType)" == "Schedule" ]] || fail "Copart auto job must remain Schedule"
[[ "$(job_field "$COPART_AUTO_JOB_NAME" properties.configuration.scheduleTriggerConfig.cronExpression)" == "$EXPECTED_COPART_AUTO_CRON" ]] || fail "Unexpected Copart auto cron"
[[ "$(job_compact_json "$COPART_AUTO_JOB_NAME" 'properties.template.containers[0].args')" == '["--copart-excel-run"]' ]] || fail "Copart auto arguments changed by another deployment"
[[ "$(job_field "$GENERIC_JOB_NAME" properties.configuration.triggerType)" == "Manual" ]] || fail "Generic legacy job must remain Manual"

environment_id="$(job_field "$COPART_AUTO_JOB_NAME" properties.environmentId)"
identity_id="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$COPART_AUTO_JOB_NAME" --query 'keys(identity.userAssignedIdentities)[0]' --output tsv)"
[[ -n "$environment_id" && "$environment_id" != "null" ]] || fail "Unable to resolve Container Apps environment"
[[ -n "$identity_id" && "$identity_id" != "null" ]] || fail "Unable to resolve managed identity"

persistence_provider="$(required_source_env Persistence__Provider)"
persistence_host="$(required_source_env Persistence__PostgreSqlHost)"
persistence_database="$(required_source_env Persistence__Database)"
persistence_identity_client_id="$(required_source_env Persistence__ManagedIdentityClientId)"
persistence_database_user="$(required_source_env Persistence__DatabaseUser)"
persistence_principal_name="$(required_source_env Persistence__RuntimePrincipalName)"
persistence_principal_object_id="$(required_source_env Persistence__RuntimePrincipalObjectId)"

if az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$SCORING_JOB_NAME" >/dev/null 2>&1; then
  scoring_image="$(job_field "$SCORING_JOB_NAME" 'properties.template.containers[0].image')"
  scoring_trigger="$(job_field "$SCORING_JOB_NAME" properties.configuration.triggerType)"
  scoring_cron="$(job_field "$SCORING_JOB_NAME" properties.configuration.scheduleTriggerConfig.cronExpression)"
  scoring_args="$(job_compact_json "$SCORING_JOB_NAME" 'properties.template.containers[0].args')"
  scoring_timeout="$(job_field "$SCORING_JOB_NAME" properties.configuration.replicaTimeout)"
  [[ "$scoring_trigger" == "Schedule" && "$scoring_cron" == "$SCORING_CRON" ]] || fail "Existing scoring schedule differs"
  [[ "$scoring_args" == '["--scoring-backfill"]' ]] || fail "Existing scoring arguments differ"
  [[ "$scoring_timeout" == "$SCORING_TIMEOUT" ]] || fail "Existing scoring timeout differs"
  if [[ "$scoring_image" != "$EXPECTED_API_IMAGE" ]]; then
    printf 'SCORING_R45_UPDATE_IMAGE_START old=%s new=%s\n' "$scoring_image" "$EXPECTED_API_IMAGE"
    az containerapp job update \
      --resource-group "$RESOURCE_GROUP" \
      --name "$SCORING_JOB_NAME" \
      --image "$EXPECTED_API_IMAGE" \
      --output none
  fi
  printf 'SCORING_R45_ALREADY_CONFIGURED=true\n'
else
  printf 'SCORING_R45_CREATE_START cron=%s maximum=%s\n' "$SCORING_CRON" "$SCORING_BATCH_MAXIMUM"
  az containerapp job create \
    --resource-group "$RESOURCE_GROUP" \
    --name "$SCORING_JOB_NAME" \
    --environment "$environment_id" \
    --trigger-type Schedule \
    --cron-expression "$SCORING_CRON" \
    --replica-timeout "$SCORING_TIMEOUT" \
    --replica-retry-limit 1 \
    --replica-completion-count 1 \
    --parallelism 1 \
    --image "$EXPECTED_API_IMAGE" \
    --registry-server "$REGISTRY_LOGIN_SERVER" \
    --registry-identity "$identity_id" \
    --mi-user-assigned "$identity_id" \
    --args --scoring-backfill \
    --env-vars \
      "Persistence__Provider=$persistence_provider" \
      "Persistence__PostgreSqlHost=$persistence_host" \
      "Persistence__Database=$persistence_database" \
      "Persistence__ManagedIdentityClientId=$persistence_identity_client_id" \
      "Persistence__DatabaseUser=$persistence_database_user" \
      "Persistence__RuntimePrincipalName=$persistence_principal_name" \
      "Persistence__RuntimePrincipalObjectId=$persistence_principal_object_id" \
      "Persistence__RunMigrations=false" \
      "Sync__Enabled=false" \
      "IaaIPilot__Enabled=false" \
      "IaaIPilot__RunOnStartup=false" \
      "IaaINational__Enabled=false" \
      "IaaINational__RunOnStartup=false" \
      "Scoring__RunOnStartup=false" \
      "Scoring__BackfillMaximumLots=$SCORING_BATCH_MAXIMUM" \
      "Scoring__BatchSize=100" \
    --output none
fi

api_image_after="$(app_field 'properties.template.containers[0].image')"
api_revision_after="$(app_field properties.latestRevisionName)"
api_identity_after="$(fingerprint_app identity)"
api_secrets_after="$(fingerprint_app properties.configuration.secrets)"
api_ingress_after="$(fingerprint_app properties.configuration.ingress)"
iaai_template_after="$(fingerprint_job "$IAAI_JOB_NAME" properties.template)"
iaai_configuration_after="$(fingerprint_job "$IAAI_JOB_NAME" properties.configuration)"
iaai_identity_after="$(fingerprint_job "$IAAI_JOB_NAME" identity)"
copart_template_after="$(fingerprint_job "$COPART_JOB_NAME" properties.template)"
copart_configuration_after="$(fingerprint_job "$COPART_JOB_NAME" properties.configuration)"
copart_identity_after="$(fingerprint_job "$COPART_JOB_NAME" identity)"
copart_auto_template_after="$(fingerprint_job "$COPART_AUTO_JOB_NAME" properties.template)"
copart_auto_configuration_after="$(fingerprint_job "$COPART_AUTO_JOB_NAME" properties.configuration)"
copart_auto_identity_after="$(fingerprint_job "$COPART_AUTO_JOB_NAME" identity)"
generic_template_after="$(fingerprint_job "$GENERIC_JOB_NAME" properties.template)"
generic_configuration_after="$(fingerprint_job "$GENERIC_JOB_NAME" properties.configuration)"
generic_identity_after="$(fingerprint_job "$GENERIC_JOB_NAME" identity)"

[[ "$api_image_after" == "$api_image_before" ]] || fail "API image changed unexpectedly"
[[ "$api_revision_after" == "$api_revision_before" ]] || fail "API revision changed unexpectedly"
[[ "$api_identity_after" == "$api_identity_before" ]] || fail "API identity changed unexpectedly"
[[ "$api_secrets_after" == "$api_secrets_before" ]] || fail "API secrets changed unexpectedly"
[[ "$api_ingress_after" == "$api_ingress_before" ]] || fail "API ingress changed unexpectedly"
[[ "$iaai_template_after" == "$iaai_template_before" && "$iaai_configuration_after" == "$iaai_configuration_before" && "$iaai_identity_after" == "$iaai_identity_before" ]] || fail "IAAI job changed unexpectedly"
[[ "$copart_template_after" == "$copart_template_before" && "$copart_configuration_after" == "$copart_configuration_before" && "$copart_identity_after" == "$copart_identity_before" ]] || fail "Copart Excel job changed unexpectedly"
[[ "$copart_auto_template_after" == "$copart_auto_template_before" && "$copart_auto_configuration_after" == "$copart_auto_configuration_before" && "$copart_auto_identity_after" == "$copart_auto_identity_before" ]] || fail "Copart auto job changed unexpectedly"
[[ "$generic_template_after" == "$generic_template_before" && "$generic_configuration_after" == "$generic_configuration_before" && "$generic_identity_after" == "$generic_identity_before" ]] || fail "Generic job changed unexpectedly"

[[ "$(job_field "$SCORING_JOB_NAME" 'properties.template.containers[0].image')" == "$EXPECTED_API_IMAGE" ]] || fail "Scoring image not applied"
[[ "$(job_field "$SCORING_JOB_NAME" properties.configuration.triggerType)" == "Schedule" ]] || fail "Scoring trigger not applied"
[[ "$(job_field "$SCORING_JOB_NAME" properties.configuration.scheduleTriggerConfig.cronExpression)" == "$SCORING_CRON" ]] || fail "Scoring cron not applied"
[[ "$(job_compact_json "$SCORING_JOB_NAME" 'properties.template.containers[0].args')" == '["--scoring-backfill"]' ]] || fail "Scoring arguments not applied"
[[ "$(job_field "$SCORING_JOB_NAME" properties.configuration.replicaTimeout)" == "$SCORING_TIMEOUT" ]] || fail "Scoring timeout not applied"
[[ "$(job_field "$SCORING_JOB_NAME" 'properties.template.containers[0].env[?name==`Persistence__RunMigrations`].value | [0]')" == "false" ]] || fail "Scoring migrations must remain disabled"
[[ "$(job_field "$SCORING_JOB_NAME" 'properties.template.containers[0].env[?name==`Sync__Enabled`].value | [0]')" == "false" ]] || fail "Scoring legacy sync must remain disabled"
[[ "$(job_field "$SCORING_JOB_NAME" 'properties.template.containers[0].env[?name==`IaaINational__RunOnStartup`].value | [0]')" == "false" ]] || fail "Scoring job must not start national IAAI"

printf 'SCORING_R45_CREATE_COMPLETED\n'
printf 'SCORING_JOB=%s\nSCORING_CRON=%s\nSCORING_BATCH_MAXIMUM=%s\n' "$SCORING_JOB_NAME" "$SCORING_CRON" "$SCORING_BATCH_MAXIMUM"
printf 'API_CHANGED=false\nIAAI_JOB_CHANGED=false\nCOPART_JOB_CHANGED=false\nCOPART_AUTO_JOB_CHANGED=false\nGENERIC_JOB_CHANGED=false\nSCORING_JOB_EXECUTION_STARTED=false\nMIGRATIONS_ENABLED=false\n'
