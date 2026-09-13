#!/usr/bin/env python3
import json
import os
from decimal import Decimal
from datetime import date, datetime

import psycopg2


def json_default(value):
    if isinstance(value, (datetime, date)):
        return value.isoformat()
    if isinstance(value, Decimal):
        return float(value)
    raise TypeError(f"Unsupported JSON value: {type(value)!r}")


conn = psycopg2.connect(
    host=os.environ["POSTGRES_HOST"],
    port=5432,
    dbname=os.environ["POSTGRES_DATABASE"],
    user=os.environ["POSTGRES_USER"],
    password=os.environ["PGPASSWORD"],
    sslmode="require",
    connect_timeout=10,
)
try:
    with conn.cursor() as cur:
        cur.execute("select to_regclass('public.seller_classifications') is not null")
        table_exists = bool(cur.fetchone()[0])
        if not table_exists:
            print(json.dumps({"table_exists": False, "total": 0, "recent": []}))
        else:
            cur.execute("select count(*) from public.seller_classifications")
            total = int(cur.fetchone()[0])
            cur.execute(
                """
                select platform, seller_name_normalized, category, confidence,
                       needs_review, evidence_type, prompt_version, updated_at
                from public.seller_classifications
                where updated_at >= now() - interval '30 minutes'
                order by updated_at desc
                limit 20
                """
            )
            recent = [
                {
                    "platform": row[0],
                    "seller_name_normalized": row[1],
                    "category": row[2],
                    "confidence": row[3],
                    "needs_review": row[4],
                    "evidence_type": row[5],
                    "prompt_version": row[6],
                    "updated_at": row[7],
                }
                for row in cur.fetchall()
            ]
            print(json.dumps({"table_exists": True, "total": total, "recent": recent}, default=json_default, indent=2))
finally:
    conn.close()
