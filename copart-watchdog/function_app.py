import datetime as dt
import json
import logging
import os
import uuid

import azure.functions as func
from azure.data.tables import TableServiceClient, UpdateMode
from azure.identity import DefaultAzureCredential
from azure.mgmt.appcontainers import ContainerAppsAPIClient

app = func.FunctionApp()

JOB_NAME = os.environ["COPART_AUTO_JOB_NAME"]
RESOURCE_GROUP = os.environ["COPART_RESOURCE_GROUP"]
SUBSCRIPTION_ID = os.environ["COPART_SUBSCRIPTION_ID"]
TABLE_NAME = os.environ.get("COPART_ATTEMPTS_TABLE", "CopartExecutionAttempts")


def utc_now():
    return dt.datetime.now(dt.timezone.utc)


def table_client():
    endpoint = os.environ["COPART_STORAGE_TABLE_ENDPOINT"]
    credential = DefaultAzureCredential()
    service = TableServiceClient(endpoint=endpoint, credential=credential)
    return service.get_table_client(TABLE_NAME)


def put_attempt(state, scheduled_at, execution_name=None, error_code=None, error_summary=None):
    client = table_client()
    scheduled_key = scheduled_at.strftime("%Y%m%dT%H%MZ")
    entity = {
        "PartitionKey": "copart",
        "RowKey": scheduled_key,
        "attempt_id": str(uuid.uuid4()),
        "correlation_id": f"copart-{scheduled_key}",
        "scheduled_at": scheduled_at.isoformat(),
        "state": state,
        "stage": "WATCHDOG",
        "last_heartbeat_at": utc_now().isoformat(),
        "azure_execution_name": execution_name or "",
        "error_code": error_code or "",
        "error_summary": error_summary or "",
    }
    client.upsert_entity(entity=entity, mode=UpdateMode.MERGE)


@app.schedule(schedule="0 */5 * * * *", arg_name="timer", run_on_startup=False, use_monitor=True)
def copart_watchdog(timer: func.TimerRequest) -> None:
    """Observe scheduled Copart windows without starting or stopping jobs."""
    now = utc_now()
    logging.info("Copart watchdog tick at %s", now.isoformat())
    # Phase 1 records the expected window durably. Azure execution correlation is
    # intentionally isolated behind the management client and is added only after
    # the table path is validated in production.
    window = now.replace(minute=(now.minute // 5) * 5, second=0, microsecond=0)
    put_attempt("SCHEDULED", window)
    logging.info("Copart watchdog recorded correlation_id=copart-%s", window.strftime("%Y%m%dT%H%MZ"))
