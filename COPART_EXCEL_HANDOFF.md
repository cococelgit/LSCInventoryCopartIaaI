# Handoff — Adaptador Copart Excel

## Objetivo

Conectar el snapshot Excel de Copart descargado por el proceso externo al mismo núcleo de normalización, elegibilidad, auditoría, persistencia y reconciliación que usa IAAI.

## Contrato existente

El punto de entrada está definido en:

`inventory-engine/src/Lsc.Inventory.Api/Sources/InventorySourcePolicy.cs`

```csharp
public sealed record CopartSnapshotEnvelope(
    string FileName,
    string Sha256,
    DateTimeOffset DownloadedAt,
    Stream Content);

public interface ICopartExcelSnapshotAdapter
{
    IAsyncEnumerable<AuctionVehicle> ReadAcceptedSnapshotAsync(
        CopartSnapshotEnvelope snapshot,
        CancellationToken cancellationToken);
}
```

## Flujo obligatorio

1. Recibir el archivo ya descargado en el servidor.
2. Verificar tamaño, extensión, SHA-256 y fecha del snapshot antes de leer filas.
3. Procesar el Excel en streaming; no cargar el archivo completo en memoria.
4. Mapear cada fila al contrato `AuctionVehicle` conservando valores raw.
5. Ejecutar `CanonicalVehicleCleaner`.
6. Ejecutar `AuctionEligibilityEvaluator` con la política v4.
7. Guardar aceptados en PostgreSQL y payload/versiones en Blob privado.
8. Registrar descartes y evidencia en la auditoría interna.
9. Aplicar reconciliación únicamente cuando el snapshot esté completo y validado.
10. Publicar la fuente como `copart` sin llamar Apibara.

## Reglas que no deben cambiarse

- Apibara está autorizado únicamente para `iaai`.
- Ningún tipo de título se descarta solo por su categoría; D09 permanece desactivada.
- Los títulos especiales se guardan y el UI los oculta por defecto hasta que el usuario los seleccione.
- La reconciliación despublica después de tres snapshots completos consecutivos sin presencia y reactiva al reaparecer.
- Un archivo parcial, corrupto o con hash repetido no puede causar despublicación.

## Pruebas mínimas requeridas

| Caso | Resultado esperado |
|---|---|
| Excel válido con filas aceptadas y descartadas | Aceptados persistidos; descartados auditados por regla. |
| Archivo de 100 MB o más | Procesamiento streaming con memoria acotada. |
| Mismo SHA-256 procesado nuevamente | Operación idempotente. |
| Snapshot incompleto | No ejecuta reconciliación destructiva. |
| Copart solicitado mediante Apibara | Falla antes de realizar la llamada HTTP. |
| Lote ausente en tres snapshots completos | Se despublica sin borrar historial. |
| Lote reaparece | Se reactiva. |

## Definition of Done

La integración queda terminada cuando una corrida controlada produce un resumen auditable de filas observadas, aceptadas, descartadas, marcadas, errores, duplicados y duración; el UI muestra la fuente Copart; y no se registra ninguna llamada Copart a Apibara.
