# LSC Inventory Engine

Servicio de inventario para **La Subasta Cubana**. Esta primera entrega consulta Apibara con scopes controlados, normaliza la respuesta y deja preparada la operación como API interna y job de ingesta en Azure Container Apps.

## Estado de la entrega

| Componente | Estado | Decisión |
|---|---:|---|
| Cliente Apibara | Implementado | Autenticación por `X-API-Key`, paginación por cursor, límite de 20 lotes por página y resiliencia HTTP. |
| Worker de ingesta | Implementado | Un scope por plataforma/estado, sin ejecuciones solapadas. |
| API interna | Implementada | Health, readiness, uso de API, snapshots de bootstrap y ejecución manual de sincronización. |
| Persistencia de bootstrap | Implementada | Memoria únicamente; evita escribir datos de negocio hasta contar con PostgreSQL. |
| Blob Storage | Preparado | Contenedores privados para payloads crudos, media, análisis y exports. |
| PostgreSQL | Pendiente | Bloqueado por el proveedor Azure; no se habilita polling productivo antes de resolverlo. |
| Despliegue Azure | Preparado | Dockerfile y manifiestos internos para Container App y Container App Job. |

## Rutas internas

| Ruta | Propósito | Seguridad prevista |
|---|---|---|
| `GET /healthz` | Liveness de plataforma | Interna. |
| `GET /readyz` | Estado de componentes | Interna. |
| `GET /api/v1/inventory/recent` | Ver snapshots de bootstrap | Interna; sustituir por lectura PostgreSQL. |
| `GET /api/v1/usage` | Consultar uso de Apibara | Interna; no expone la clave. |
| `POST /internal/sync/run` | Ejecutar un scope bajo demanda | Interna; se protegerá por identidad de servicio al activar el API. |

## Configuración

La clave de Apibara **nunca** se almacena en `appsettings.json`, el repositorio ni el frontend. El despliegue de Azure consume el secreto desde Key Vault mediante la identidad administrada `id-lsc-inventory-runtime-prod`.

```text
Apibara__ApiKey              Referencia de secreto Key Vault
Apibara__BaseUrl             https://apibara.tech/api/v1/vehicle-auction/
Apibara__PageSize            20
Sync__Platforms__0           copart
Sync__States__0              FL
Sync__PagesPerScope          1
Sync__Enabled                false hasta activar PostgreSQL
```

## Validación local

```bash
cd /home/ubuntu/lsc-inventory-engine
dotnet build Lsc.Inventory.sln -c Release

ASPNETCORE_URLS=http://127.0.0.1:8090 \
ASPNETCORE_ENVIRONMENT=Production \
Apibara__ApiKey=bootstrap-placeholder \
Sync__Enabled=false \
dotnet run --project src/Lsc.Inventory.Api/Lsc.Inventory.Api.csproj --no-launch-profile -c Release
```

Después, consulta `http://127.0.0.1:8090/healthz`.

## Activación de producción

La activación requiere cuatro condiciones. Primero, Azure debe permitir provisionar PostgreSQL Flexible Server. Segundo, se debe rotar la clave que fue compartida anteriormente y guardar la nueva versión en Key Vault como `apibara-api-key`. Tercero, se implementará el adaptador PostgreSQL y las migraciones. Por último, se publica una imagen versionada en ACR y se despliegan los manifiestos de `infra/` con la identidad administrada existente.

> No se activa la sincronización recurrente ni se ejecuta un job de ingesta sobre producción antes de que el adaptador PostgreSQL, la auditoría de snapshots y los límites de cuota estén conectados.
