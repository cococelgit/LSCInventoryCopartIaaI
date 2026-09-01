# Despliegue del contrato IAAI ampliado

## Estado de seguridad

Copart permanece deshabilitado en Apibara. El job IAAI es manual y el enriquecimiento de detalles tiene un límite explícito de 1,000. No agregue secretos al repositorio: el runtime debe seguir leyendo referencias de Key Vault mediante la identidad administrada actual.

## Precondiciones

La cuenta Azure debe tener acceso al tenant `ccfdc482-7c38-458c-b7b7-b7967a122f1d` y a la suscripción **LSC Inventory Feed Project**. El build local y las pruebas ya fueron aprobados en el commit que contiene este archivo.

## Flujo Cloud Shell de un solo comando

La rama de liberación `release/azure-iaai-extended-20260826` une de forma no destructiva el historial público del adaptador Copart Excel con el monorepo integrado. Para evitar solicitudes de credenciales Git en Cloud Shell, se distribuye un paquete público inmutable con un manifiesto de liberación. El script `cloud-shell-deploy-iaai-extended.sh` valida ese manifiesto antes de cambiar Azure, usa la etiqueta fija `iaai-extended-20260826r1` y solo actualiza la imagen de la API de lectura y del job IAAI manual. No ejecuta el job, no cambia secretos, redes, identidades administradas ni programación.

Ejecutar el script desde el paquete en una Cloud Shell ya autenticada. Si cualquiera de las comprobaciones de tenant, suscripción, manifiesto o recursos no coincide, el script termina antes de construir o actualizar recursos.

## Secuencia de despliegue

Ejecute desde la raíz `inventory-engine`:

```bash
az acr build \
  --registry acrlscinvprodeus2 \
  --image lsc-inventory:a63b6d7 \
  --file Dockerfile \
  .

az containerapp update \
  --resource-group rg-lsc-inventory-prod \
  --name ca-lsc-inventory-api-prod \
  --image acrlscinvprodeus2.azurecr.io/lsc-inventory:a63b6d7

az containerapp job update \
  --resource-group rg-lsc-inventory-prod \
  --name job-lsc-iaai-pilot-prod \
  --image acrlscinvprodeus2.azurecr.io/lsc-inventory:a63b6d7
```

No habilite `job-lsc-inventory-scheduled-prod` ni configure `Sync__Platform=copart`: Copart se ingestará exclusivamente mediante el adaptador Excel.

## Validación posterior

Primero consulte el endpoint público a través del bridge LSC y confirme que `platform=iaai` y que los campos extendidos se serializan cuando el proveedor los entrega. Después inicie solo una ejecución manual del job IAAI, espere su finalización y valide los siguientes grupos:

| Grupo | Campos esperados |
|---|---|
| Condición | daños, run & drive, llaves, odómetro, pérdida primaria/secundaria |
| Especificaciones | body style, color, motor, transmisión, combustible y tracción |
| Venta | fecha, puja, buy now, estimado y documento de venta |
| Procedencia | facility, estado y vendedor cuando IAAI lo reporte |
| Media | URLs HTTPS seguras de thumbnails y fotos |

Si la ejecución falla, conserve los logs y no active cron ni Copart. La política de elegibilidad, limpieza y reconciliación sigue siendo determinística y no se debe modificar durante el despliegue.
