#!/usr/bin/env bash
# Replaces only the legacy FL process in the scheduled Copart auto job.
# It never starts a job and aborts if another deployment changed the expected baseline.
set -Eeuo pipefail

readonly SUBSCRIPTION_ID="99edf3d1-a651-4d2d-af8e-d44a3360f584"
readonly RESOURCE_GROUP="rg-lsc-inventory-prod"
readonly API_NAME="ca-lsc-inventory-api-prod"
readonly AUTO_JOB_NAME="job-lsc-copart-auto-prod"
readonly COPART_EXCEL_JOB_NAME="job-lsc-copart-excel-prod"
readonly IAAI_JOB_NAME="job-lsc-iaai-pilot-prod"
readonly GENERIC_JOB_NAME="job-lsc-inventory-ingestion-prod"
readonly EXPECTED_API_IMAGE="acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:inventory-api-integrated-r22-copart-media-https"
readonly EXPECTED_IAAI_IMAGE="acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:iaai-cursor-recovery-r14"
readonly EXPECTED_IAAI_CRON="15,45 * * * *"

fail() { printf 'COPART_AUTO_R21_ABORT: %s\n' "$*" >&2; exit 1; }
job_json() { az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$1" --query "$2" --output json; }
job_field() { az containerapp job show --resource-group "$RESOURCE_GROUP" --name "$1" --query "$2" --output tsv; }
app_field() { az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query "$1" --output tsv; }
hash_json() { jq -S . | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }; }
env_value() { printf '%s' "$1" | jq -r --arg name "$2" '.containers[0].env[]? | select(.name == $name) | (.value // .secretRef // empty)' | head -n 1; }
sanitize_auto_template() {
  jq -S '
    del(.containers[0].args)
    | .containers[0].env |= map(select(.name != "Sync__Enabled" and .name != "IaaIPilot__Enabled" and .name != "IaaIPilot__RunOnStartup" and .name != "IaaINational__Enabled" and .name != "IaaINational__RunOnStartup" and .name != "Persistence__RunMigrations"))
  ' | sha256sum | { read -r digest _; printf '%s\n' "$digest"; }
}
is_empty_args() { [[ "$1" == "[]" || "$1" == "null" || -z "$1" ]]; }
is_half_hour_cron() { [[ "$1" == "*/30 * * * *" || "$1" == "0,30 * * * *" ]]; }

command -v az >/dev/null 2>&1 || fail "Azure CLI is unavailable"
command -v jq >/dev/null 2>&1 || fail "jq is unavailable"
command -v sha256sum >/dev/null 2>&1 || fail "sha256sum is unavailable"
az extension add --name containerapp --upgrade --only-show-errors
az account set --subscription "$SUBSCRIPTION_ID"

api_before="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query '{image:properties.template.containers[0].image,revision:properties.latestRevisionName,identity:identity,ingress:properties.configuration.ingress,secrets:properties.configuration.secrets}' --output json)"
auto_before="$(job_json "$AUTO_JOB_NAME" '{template:properties.template,configuration:properties.configuration,identity:identity}')"
excel_before="$(job_json "$COPART_EXCEL_JOB_NAME" '{template:properties.template,configuration:properties.configuration,identity:identity}')"
iaai_before="$(job_json "$IAAI_JOB_NAME" '{template:properties.template,configuration:properties.configuration,identity:identity}')"
generic_before="$(job_json "$GENERIC_JOB_NAME" '{template:properties.template,configuration:properties.configuration,identity:identity}')"

api_image_before="$(printf '%s' "$api_before" | jq -r '.image // empty')"
auto_trigger_before="$(printf '%s' "$auto_before" | jq -r '.configuration.triggerType // empty')"
auto_cron_before="$(printf '%s' "$auto_before" | jq -r '.configuration.scheduleTriggerConfig.cronExpression // empty')"
auto_args_before="$(printf '%s' "$auto_before" | jq -c '.template.containers[0].args // null')"
auto_command_before="$(printf '%s' "$auto_before" | jq -c '.template.containers[0].command // null')"
auto_template_guard_before="$(printf '%s' "$auto_before" | jq '.template' | sanitize_auto_template)"
auto_configuration_guard_before="$(printf '%s' "$auto_before" | jq '.configuration' | hash_json)"
auto_identity_guard_before="$(printf '%s' "$auto_before" | jq '.identity' | hash_json)"
excel_guard_before="$(printf '%s' "$excel_before" | hash_json)"
iaai_guard_before="$(printf '%s' "$iaai_before" | hash_json)"
generic_guard_before="$(printf '%s' "$generic_before" | hash_json)"

[[ "$api_image_before" == "$EXPECTED_API_IMAGE" ]] || fail "API image changed by another deployment: ${api_image_before:-empty}"
[[ "$auto_trigger_before" == "Schedule" ]] || fail "Copart auto job is not Schedule: ${auto_trigger_before:-empty}"
is_half_hour_cron "$auto_cron_before" || fail "Copart auto cron is not a half-hour cadence: ${auto_cron_before:-empty}"
is_empty_args "$auto_args_before" || fail "Copart auto command arguments changed by another deployment: $auto_args_before"
[[ "$(env_value "$(printf '%s' "$auto_before" | jq '.template')" Sync__Enabled)" == "true" ]] || fail "Legacy Sync__Enabled must be true before replacement"
[[ -n "$(env_value "$(printf '%s' "$auto_before" | jq '.template')" CopartExcel__AccountUrl)" ]] || fail "Copart Excel account URL is not configured"
[[ -n "$(env_value "$(printf '%s' "$auto_before" | jq '.template')" CopartExcel__ContainerName)" ]] || fail "Copart Excel container is not configured"
[[ "$(printf '%s' "$excel_before" | jq -r '.configuration.triggerType // empty')" == "Manual" ]] || fail "Manual Copart Excel job changed unexpectedly"
[[ "$(printf '%s' "$iaai_before" | jq -r '.configuration.triggerType // empty')" == "Schedule" ]] || fail "National IAAI job is not scheduled"
[[ "$(printf '%s' "$iaai_before" | jq -r '.configuration.scheduleTriggerConfig.cronExpression // empty')" == "$EXPECTED_IAAI_CRON" ]] || fail "National IAAI cron changed unexpectedly"
[[ "$(printf '%s' "$iaai_before" | jq -r '.template.containers[0].image // empty')" == "$EXPECTED_IAAI_IMAGE" ]] || fail "National IAAI image changed unexpectedly"
[[ "$(printf '%s' "$generic_before" | jq -r '.configuration.triggerType // empty')" == "Manual" ]] || fail "Generic legacy job changed unexpectedly"

printf 'COPART_AUTO_R21_UPDATE_START mode=copart-excel-only cron=%s\n' "$auto_cron_before"
az containerapp job update \
  --resource-group "$RESOURCE_GROUP" \
  --name "$AUTO_JOB_NAME" \
  --args "--copart-excel-run" \
  --set-env-vars \
    Sync__Enabled=false \
    IaaIPilot__Enabled=false \
    IaaIPilot__RunOnStartup=false \
    IaaINational__Enabled=false \
    IaaINational__RunOnStartup=false \
    Persistence__RunMigrations=false \
  --output none

api_after="$(az containerapp show --resource-group "$RESOURCE_GROUP" --name "$API_NAME" --query '{image:properties.template.containers[0].image,revision:properties.latestRevisionName,identity:identity,ingress:properties.configuration.ingress,secrets:properties.configuration.secrets}' --output json)"
auto_after="$(job_json "$AUTO_JOB_NAME" '{template:properties.template,configuration:properties.configuration,identity:identity}')"
excel_after="$(job_json "$COPART_EXCEL_JOB_NAME" '{template:properties.template,configuration:properties.configuration,identity:identity}')"
iaai_after="$(job_json "$IAAI_JOB_NAME" '{template:properties.template,configuration:properties.configuration,identity:identity}')"
generic_after="$(job_json "$GENERIC_JOB_NAME" '{template:properties.template,configuration:properties.configuration,identity:identity}')"

auto_args_after="$(printf '%s' "$auto_after" | jq -c '.template.containers[0].args // null')"
auto_command_after="$(printf '%s' "$auto_after" | jq -c '.template.containers[0].command // null')"
auto_template_guard_after="$(printf '%s' "$auto_after" | jq '.template' | sanitize_auto_template)"
auto_configuration_guard_after="$(printf '%s' "$auto_after" | jq '.configuration' | hash_json)"
auto_identity_guard_after="$(printf '%s' "$auto_after" | jq '.identity' | hash_json)"
excel_guard_after="$(printf '%s' "$excel_after" | hash_json)"
iaai_guard_after="$(printf '%s' "$iaai_after" | hash_json)"
generic_guard_after="$(printf '%s' "$generic_after" | hash_json)"

[[ "$(printf '%s' "$api_after" | hash_json)" == "$(printf '%s' "$api_before" | hash_json)" ]] || fail "API changed unexpectedly"
[[ "$auto_args_after" == '["--copart-excel-run"]' ]] || fail "Copart Excel arguments were not applied: $auto_args_after"
[[ "$auto_command_after" == "$auto_command_before" ]] || fail "Copart auto command changed unexpectedly"
[[ "$auto_template_guard_after" == "$auto_template_guard_before" ]] || fail "Copart auto template changed outside approved args/env"
[[ "$auto_configuration_guard_after" == "$auto_configuration_guard_before" ]] || fail "Copart auto configuration changed unexpectedly"
[[ "$auto_identity_guard_after" == "$auto_identity_guard_before" ]] || fail "Copart auto identity changed unexpectedly"
[[ "$(env_value "$(printf '%s' "$auto_after" | jq '.template')" Sync__Enabled)" == "false" ]] || fail "Legacy Sync__Enabled remains active"
[[ "$(env_value "$(printf '%s' "$auto_after" | jq '.template')" IaaIPilot__Enabled)" == "false" ]] || fail "IAAI pilot became active"
[[ "$(env_value "$(printf '%s' "$auto_after" | jq '.template')" IaaINational__Enabled)" == "false" ]] || fail "National IAAI was enabled inside Copart auto job"
[[ "$(env_value "$(printf '%s' "$auto_after" | jq '.template')" Persistence__RunMigrations)" == "false" ]] || fail "Migrations became active"
[[ "$excel_guard_after" == "$excel_guard_before" ]] || fail "Manual Copart Excel job changed unexpectedly"
[[ "$iaai_guard_after" == "$iaai_guard_before" ]] || fail "National IAAI job changed unexpectedly"
[[ "$generic_guard_after" == "$generic_guard_before" ]] || fail "Generic legacy job changed unexpectedly"

printf '%s\n' 'COPART_AUTO_R21_COMPLETED'
printf '%s\n' "COPART_AUTO_CRON=$auto_cron_before"
printf '%s\n' 'COPART_AUTO_MODE=copart-excel-only'
printf '%s\n' 'LEGACY_FL_SYNC_ENABLED=false'
printf '%s\n' 'API_CHANGED=false'
printf '%s\n' 'NATIONAL_IAAI_CHANGED=false'
printf '%s\n' 'MANUAL_COPART_EXCEL_CHANGED=false'
printf '%s\n' 'GENERIC_JOB_CHANGED=false'
printf '%s\n' 'JOB_EXECUTION_STARTED=false'
