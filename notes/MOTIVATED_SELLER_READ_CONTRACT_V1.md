# Contrato interno de lectura — Motivated Sellers v1

## Estado y alcance

Este contrato describe una lectura interna, protegida y de solo lectura sobre las tablas Copart ya existentes. No crea tablas, no ejecuta backfill, no cambia elegibilidad, no modifica lifecycle y no activa filtros, badges ni timeline en el portal público. La implementación debe permanecer detrás del token interno existente y de un feature flag independiente.

La fuente de verdad es PostgreSQL, no el frontend. El alcance es exclusivamente `platform = copart` y las filas deben respetar la disponibilidad oficial del inventario (`is_active = true`) antes de ser devueltas a una superficie de búsqueda.

## Evidencia actual disponible

Las tablas existentes son `copart_lot_observations`, `copart_auction_attempts` y `copart_lot_motivation_signals`. La observación es evidencia de presencia en un snapshot completo. Un intento se agrupa por `lot_key` y `auction_at`. Una venta solo puede marcarse como `sold_confirmed` si el origen reportó un precio de venta positivo. La desaparición de un lote nunca se interpreta como no venta.

La señal usa el modelo existente `copart-auction-history-v1`, con `score_components` como explicación de cada componente. La señal no afirma intención, presión financiera ni obligación del vendedor.

## Operación de detalle por lote

La lectura interna propuesta es:

```text
GET /internal/copart/auction-history/{lotKey}
Authorization: Bearer <INVENTORY_API_TOKEN>
```

El `lotKey` debe enlazarse como parámetro de ruta; no se acepta VIN, URL de Copart ni texto libre como clave primaria de esta lectura. La respuesta debe devolver `404` si no existe historial elegible para el lote o si el lote no cumple el filtro de disponibilidad activo.

Respuesta conceptual:

```json
{
  "lotKey": "copart:12345678",
  "platform": "copart",
  "signal": {
    "attemptCount": 3,
    "relistedInferredCount": 2,
    "score": 65,
    "level": "high",
    "firstAttemptAt": "2026-08-01T14:00:00Z",
    "lastAttemptAt": "2026-08-29T14:00:00Z",
    "lastBidUsd": 18000,
    "historicalMaximumBidUsd": 20000,
    "scoreComponents": {
      "relistingEvidencePresent": true,
      "saleConfirmed": false,
      "modelVersion": "copart-auction-history-v1"
    }
  },
  "attempts": [
    {
      "attemptNumber": 1,
      "auctionAt": "2026-08-01T14:00:00Z",
      "firstObservedAt": "2026-07-30T15:00:00Z",
      "lastObservedAt": "2026-08-01T13:30:00Z",
      "firstBidUsd": 17500,
      "lastBidUsd": 17500,
      "maximumBidUsd": 17500,
      "buyNowUsd": 22000,
      "salePriceUsd": null,
      "outcome": "relisted_inferred",
      "evidenceLevel": "inferred_from_reappearance",
      "outcomeEvidence": "The same Copart lot reappeared with a later auction date.",
      "observationCount": 2
    }
  ],
  "disclaimer": "Señal de oportunidad basada en historial de subasta. La evidencia no confirma la intención o disposición final del vendedor."
}
```

Los nombres exactos de serialización deben seguir la convención JSON existente del API. El ejemplo es de contrato, no un registro real y no debe convertirse en fixture de negocio ni en dato seed.

## Reglas de seguridad y evidencia

`unknown` debe mostrarse como resultado no confirmado, nunca como no vendido. `relisted_inferred` debe incluir la nota de que la reaparición es una inferencia y no confirma el resultado de la subasta anterior. `sold_confirmed` no debe recibir señal comercial activa. Si `relisted_inferred_count = 0`, la señal debe conservar score cero aunque existan varios intentos, antigüedad o variación de pujas.

La respuesta no debe incluir VIN completo, payload crudo, URL de Copart, credenciales, seller raw ni datos que no formen parte del contrato. El frontend no debe recalcular el score: debe leer `scoreComponents` y `modelVersion` producidos por el backend.

## Listado futuro

El endpoint de detalle puede implementarse primero. Una futura integración de listado debe unir la señal por `lot_key` después de aplicar `is_active = true`, respetar paginación y no reactivar lotes históricos. Los filtros recomendados son `level in ('medium','high')`, con `watch` opcional; no se habilitan en este sprint.

## Gates de aceptación

La implementación no queda lista hasta que las pruebas demuestren que el token inválido recibe `401`, un lote IAAI no puede devolver historial Copart, un lote inactivo no reaparece por tener señal, `unknown` conserva copy no concluyente, `relisted_inferred` devuelve evidencia y disclaimer, `sold_confirmed` no aparece como oportunidad activa y la consulta no expone VIN, URL ni payload raw.

El despliegue de este contrato debe permanecer en Staging. Production no debe recibir endpoint ni cambio de UI hasta que el piloto real y las pruebas de regresión estén aprobados explícitamente.

## Fuente de datos

La definición se basa en `inventory-engine/notes/copart_auction_attempt_history_v1.md`, `notes/HANDOFF_PORTAL_AGENT_COPART_AUCTION_HISTORY.md`, la interfaz `IInventorySnapshotStore` y la implementación de `PostgresSnapshotStore` del repositorio actual.
