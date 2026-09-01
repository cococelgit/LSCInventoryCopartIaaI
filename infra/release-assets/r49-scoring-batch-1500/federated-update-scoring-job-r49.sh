#!/usr/bin/env bash
# Updates only the scheduled LSC Pre-grade pilot using the Microsoft.App/jobs ARM resource.
# It never updates the Inventory API or the Copart/IAAI jobs and never starts a job execution.
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
readonly SCORING_BATCH_MAXIMUM="1500"
readonly JOB_API_VERSION="2024-03-01"

fail() { printf 'SCORING_R49_BATCH_1500_ABORT: %s\n' "$*" >&2; exit 1; }
app_field() { az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "$1" --output tsv; }
job_field() { az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$1" --query "$2" --output tsv; }
job_compact_json() { az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$1" --query "$2" --output json | tr -d '\r\n\t '; }
fingerprint_app() { az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "$1" --output json | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }; }
fingerprint_job() { az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$1" --query "$2" --output json | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }; }
source_env() { job_field "$COPART_AUTO_JOB_NAME" "properties.template.containers[0].env[?name=='$1'].value | [0]"; }
required_source_env() {
  local env_name="$1" value
  value="$(source_env "$env_name")"
  [[ -n "$value" && "$value" != "null" ]] || fail "Missing required non-secret source environment variable ${env_name}"
  printf '%s\n' "$value"
}

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
command -v jq >/dev/null 2>&1 || fail "jq is unavailable"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is unavailable"
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

az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$SCORING_JOB_NAME" >/dev/null 2>&1 \
  || fail "Expected existing scoring job r47 was not found; refusing to create a new resource"
[[ "$(job_field "$SCORING_JOB_NAME" properties.provisioningState)" == "Succeeded" ]] \
  || fail "Existing scoring job is not provisioned successfully"
[[ "$(job_field "$SCORING_JOB_NAME" properties.configuration.triggerType)" == "Schedule" ]] \
  || fail "Existing scoring job trigger differs from Schedule"
[[ "$(job_field "$SCORING_JOB_NAME" properties.configuration.scheduleTriggerConfig.cronExpression)" == "$SCORING_CRON" ]] \
  || fail "Existing scoring job cron differs from the approved cadence"
[[ "$(job_compact_json "$SCORING_JOB_NAME" 'properties.template.containers[0].args')" == '["--scoring-backfill"]' ]] \
  || fail "Existing scoring job arguments differ from scoring backfill"

environment_id="$(job_field "$COPART_AUTO_JOB_NAME" properties.environmentId)"
identity_id="$(az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$COPART_AUTO_JOB_NAME" --query 'keys(identity.userAssignedIdentities)[0]' --output tsv)"
subscription_id="$(az account show --query id --output tsv)"
[[ -n "$environment_id" && "$environment_id" != "null" ]] || fail "Unable to resolve Container Apps environment"
[[ -n "$identity_id" && "$identity_id" != "null" ]] || fail "Unable to resolve managed identity"
[[ -n "$subscription_id" && "$subscription_id" != "null" ]] || fail "Unable to resolve subscription"

persistence_provider="$(required_source_env Persistence__Provider)"
persistence_host="$(required_source_env Persistence__PostgreSqlHost)"
persistence_database="$(required_source_env Persistence__Database)"
persistence_identity_client_id="$(required_source_env Persistence__ManagedIdentityClientId)"
persistence_database_user="$(required_source_env Persistence__DatabaseUser)"
persistence_principal_name="$(required_source_env Persistence__RuntimePrincipalName)"
persistence_principal_object_id="$(required_source_env Persistence__RuntimePrincipalObjectId)"
blob_audit_account_url="$(required_source_env BlobAudit__AccountUrl)"
blob_audit_container_name="$(required_source_env BlobAudit__ContainerName)"

manifest="/tmp/${SCORING_JOB_NAME}-r49.json"
jq -n \
  --arg location "eastus2" \
  --arg environment_id "$environment_id" \
  --arg identity_id "$identity_id" \
  --arg registry "$REGISTRY_LOGIN_SERVER" \
  --arg image "$EXPECTED_API_IMAGE" \
  --arg cron "$SCORING_CRON" \
  --arg provider "$persistence_provider" \
  --arg host "$persistence_host" \
  --arg database "$persistence_database" \
  --arg mi_client_id "$persistence_identity_client_id" \
  --arg database_user "$persistence_database_user" \
  --arg principal_name "$persistence_principal_name" \
  --arg principal_object_id "$persistence_principal_object_id" \
  --arg blob_audit_account_url "$blob_audit_account_url" \
  --arg blob_audit_container_name "$blob_audit_container_name" \
  --argjson timeout "$SCORING_TIMEOUT" \
  --argjson maximum "$SCORING_BATCH_MAXIMUM" \
  '{
    location: $location,
    identity: { type: "UserAssigned", userAssignedIdentities: { ($identity_id): {} } },
    properties: {
      environmentId: $environment_id,
      configuration: {
        triggerType: "Schedule",
        replicaTimeout: $timeout,
        replicaRetryLimit: 1,
        scheduleTriggerConfig: { cronExpression: $cron, parallelism: 1, replicaCompletionCount: 1 },
        registries: [{ server: $registry, identity: $identity_id }]
      },
      template: {
        containers: [{
          name: "lsc-inventory-scoring",
          image: $image,
          args: ["--scoring-backfill"],
          resources: { cpu: 0.25, memory: "0.5Gi" },
          env: [
            {name:"Persistence__Provider",value:$provider},
            {name:"Persistence__PostgreSqlHost",value:$host},
            {name:"Persistence__Database",value:$database},
            {name:"Persistence__ManagedIdentityClientId",value:$mi_client_id},
            {name:"Persistence__DatabaseUser",value:$database_user},
            {name:"Persistence__RuntimePrincipalName",value:$principal_name},
            {name:"Persistence__RuntimePrincipalObjectId",value:$principal_object_id},
            {name:"BlobAudit__AccountUrl",value:$blob_audit_account_url},
            {name:"BlobAudit__ContainerName",value:$blob_audit_container_name},
            {name:"Persistence__RunMigrations",value:"false"},
            {name:"Sync__Enabled",value:"false"},
            {name:"IaaIPilot__Enabled",value:"false"},
            {name:"IaaIPilot__RunOnStartup",value:"false"},
            {name:"IaaINational__Enabled",value:"false"},
            {name:"IaaINational__RunOnStartup",value:"false"},
            {name:"Scoring__RunOnStartup",value:"false"},
            {name:"Scoring__BackfillMaximumLots",value:($maximum|tostring)},
            {name:"Scoring__BatchSize",value:"100"}
          ]
        }],
        initContainers: [],
        volumes: []
      }
    },
    tags: { "lsc-component":"scoring-pilot", "lsc-managed":"deployment-script", "lsc-execution-manual":"false" }
  }' > "$manifest"

jq empty "$manifest" || fail "Scoring job manifest is invalid JSON"
job_uri="https://management.azure.com/subscriptions/${subscription_id}/resourceGroups/${RESOURCE_GROUP}/providers/Microsoft.App/jobs/${SCORING_JOB_NAME}?api-version=${JOB_API_VERSION}"
printf 'SCORING_R49_BATCH_1500_UPDATE_START cron=%s maximum=%s\n' "$SCORING_CRON" "$SCORING_BATCH_MAXIMUM"
az rest --method put --url "$job_uri" --body "@${manifest}" --headers 'Content-Type=application/json' --output none

for attempt in {1..24}; do
  if az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$SCORING_JOB_NAME" >/dev/null 2>&1; then
    provisioning="$(job_field "$SCORING_JOB_NAME" properties.provisioningState)"
    [[ "$provisioning" == "Succeeded" ]] && break
    [[ "$provisioning" == "Failed" ]] && fail "Scoring job provisioning failed"
  fi
  sleep 5
done

[[ "$(job_field "$SCORING_JOB_NAME" properties.provisioningState)" == "Succeeded" ]] || fail "Scoring job did not reach Succeeded before timeout"
[[ "$(job_field "$SCORING_JOB_NAME" 'properties.template.containers[0].image')" == "$EXPECTED_API_IMAGE" ]] || fail "Scoring image not applied"
[[ "$(job_field "$SCORING_JOB_NAME" properties.configuration.triggerType)" == "Schedule" ]] || fail "Scoring trigger not applied"
[[ "$(job_field "$SCORING_JOB_NAME" properties.configuration.scheduleTriggerConfig.cronExpression)" == "$SCORING_CRON" ]] || fail "Scoring cron not applied"
[[ "$(job_compact_json "$SCORING_JOB_NAME" 'properties.template.containers[0].args')" == '["--scoring-backfill"]' ]] || fail "Scoring arguments not applied"
[[ "$(job_field "$SCORING_JOB_NAME" properties.configuration.replicaTimeout)" == "$SCORING_TIMEOUT" ]] || fail "Scoring timeout not applied"
[[ "$(job_field "$SCORING_JOB_NAME" 'properties.template.containers[0].env[?name==`Persistence__RunMigrations`].value | [0]')" == "false" ]] || fail "Scoring migrations must remain disabled"
[[ "$(job_field "$SCORING_JOB_NAME" 'properties.template.containers[0].env[?name==`Sync__Enabled`].value | [0]')" == "false" ]] || fail "Scoring legacy sync must remain disabled"
[[ "$(job_field "$SCORING_JOB_NAME" 'properties.template.containers[0].env[?name==`IaaINational__RunOnStartup`].value | [0]')" == "false" ]] || fail "Scoring job must not start national IAAI"
[[ -n "$(job_field "$SCORING_JOB_NAME" 'properties.template.containers[0].env[?name==`BlobAudit__AccountUrl`].value | [0]')" ]] || fail "Blob Audit account URL was not applied"
[[ "$(job_field "$SCORING_JOB_NAME" 'properties.template.containers[0].env[?name==`BlobAudit__ContainerName`].value | [0]')" == "raw-apibara" ]] || fail "Blob Audit container was not applied"

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

[[ "$api_image_after" == "$api_image_before" && "$api_revision_after" == "$api_revision_before" && "$api_identity_after" == "$api_identity_before" && "$api_secrets_after" == "$api_secrets_before" && "$api_ingress_after" == "$api_ingress_before" ]] || fail "API changed unexpectedly"
[[ "$iaai_template_after" == "$iaai_template_before" && "$iaai_configuration_after" == "$iaai_configuration_before" && "$iaai_identity_after" == "$iaai_identity_before" ]] || fail "IAAI job changed unexpectedly"
[[ "$copart_template_after" == "$copart_template_before" && "$copart_configuration_after" == "$copart_configuration_before" && "$copart_identity_after" == "$copart_identity_before" ]] || fail "Copart Excel job changed unexpectedly"
[[ "$copart_auto_template_after" == "$copart_auto_template_before" && "$copart_auto_configuration_after" == "$copart_auto_configuration_before" && "$copart_auto_identity_after" == "$copart_auto_identity_before" ]] || fail "Copart auto job changed unexpectedly"
[[ "$generic_template_after" == "$generic_template_before" && "$generic_configuration_after" == "$generic_configuration_before" && "$generic_identity_after" == "$generic_identity_before" ]] || fail "Generic job changed unexpectedly"

printf 'SCORING_R49_BATCH_1500_UPDATE_COMPLETED\nSCORING_JOB=%s\nSCORING_CRON=%s\nSCORING_BATCH_MAXIMUM=%s\n' "$SCORING_JOB_NAME" "$SCORING_CRON" "$SCORING_BATCH_MAXIMUM"
printf 'API_CHANGED=false\nIAAI_JOB_CHANGED=false\nCOPART_JOB_CHANGED=false\nCOPART_AUTO_JOB_CHANGED=false\nGENERIC_JOB_CHANGED=false\nSCORING_JOB_EXECUTION_STARTED=false\nMIGRATIONS_ENABLED=false\n'
