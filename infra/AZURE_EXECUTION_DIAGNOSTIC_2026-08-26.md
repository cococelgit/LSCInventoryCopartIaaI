# Diagnóstico de ejecución Azure — 26 de agosto de 2026

## Evidencia observada

- El portal de Azure está autenticado visualmente como `osniel@cococel.com` en **Default Directory**.
- Cloud Shell integrada materializó una sesión Bash efímera y mostró el prompt `osniel [ ~ ]$`.
- La automatización del navegador no expone el control de terminal como un campo editable; la única superficie accesible aparece como un divisor. Un intento de escribir `az account show` quedó asociado al divisor y **no produjo salida visible ni se considera ejecutado**.
- El acceso directo a Cloud Shell agotó el tiempo de respuesta del navegador y no cambió la página del portal.
- En el sandbox local, `az account show` sigue respondiendo `Please run 'az login' to setup account.`

## Conclusión operativa

No se ha lanzado ningún build, actualización de Container App, actualización de job ni ejecución IAAI en esta fase. El siguiente intento debe usar una vía autenticada que permita observar comandos y resultados, sin alterar secretos, red privada ni el job programado de Copart.

## Evidencia adicional: interrupción de Cloud Shell

La captura recibida durante el intento del paquete HTTPS muestra exclusivamente el diálogo **“Cloud Shell disconnected”** con el texto **“Would you like to reconnect?”** y las acciones **Connect** y **Close**. No muestra salida del script, error de Azure CLI, ni actividad de build. Por tanto, la desconexión ocurrió en la sesión de terminal o su canal de navegador antes de que se pudiera observar una etapa de despliegue; no se interpreta como un fallo del build ni como confirmación de una modificación productiva.

## Verificación de recursos mediante portal

El portal confirma la suscripción **LSC Inventory Feed Project** y el registro Premium `acrlscinvprodeus2.azurecr.io` en East US 2. Los repositorios presentes son `copart-downloader` y `lsc-inventory-engine`; no existe el repositorio `lsc-inventory` indicado por el runbook anterior, por lo que no se debe ejecutar esa referencia hasta corregirla con evidencia. El registro no tiene tareas ACR configuradas.

La API `ca-lsc-inventory-api-prod` está **Running** y usa una identidad administrada para obtener imágenes del registro. El job `job-lsc-iaai-pilot-prod` está **Succeeded**, con trigger **Manual**, paralelismo 1 y completion count 1. La vista del portal expone el botón Run now, pero no fue utilizado; no se observó ninguna ejecución creada durante los intentos fallidos de Cloud Shell.

## Bloqueo del asistente de despliegue continuo

El portal puede autenticar la cuenta GitHub `cococelgit` y seleccionar el repositorio/branch de liberación, pero al elegir `acrlscinvprodeus2` muestra: **“Cannot perform credential operations ... as admin user is disabled.”** La ruta visual intenta usar operaciones de credenciales administrativas del ACR. Ese usuario seguirá deshabilitado: no se habilita como atajo de despliegue.

La alternativa preparada es un `Microsoft.Resources/deploymentScripts` de Azure CLI autenticado con una identidad administrada asignada por el usuario. Microsoft documenta que ese recurso admite una identidad administrada asignada por el usuario y que la propagación de permisos otorgados en la misma plantilla se reintenta antes de invocar el script. Fuente oficial: https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/deployment-script-template

El alcance previsto se limita al registro `acrlscinvprodeus2`, a `ca-lsc-inventory-api-prod` y a `job-lsc-iaai-pilot-prod`. Para ACR se usan los roles de tareas y escritura de repositorio documentados para builds/push; para la API y el job se usan los roles específicos de Container Apps a nivel de recurso. La documentación de ACR identifica `Container Registry Tasks Contributor` para quick builds y `Container Registry Repository Writer` o `AcrPush`, según el modo RBAC/ABAC del registro. Fuentes oficiales: https://learn.microsoft.com/en-us/azure/container-registry/container-registry-rbac-built-in-roles-overview y https://learn.microsoft.com/en-us/azure/container-registry/container-registry-authentication-managed-identity

## Evidencia posterior al reintento controlado

- La comprobación solicitada `az account show --query '{subscription:name,tenant:tenantId,user:user.name}' --output json` se ejecutó en el sandbox local y respondió exactamente: `Please run 'az login' to setup account.` Por tanto, la CLI local continúa sin una sesión Azure autenticada.
- El segundo intento federado superó la creación de la identidad asignada por usuario y sus role assignments limitados, pero falló antes de actualizar API o job porque ACR no pudo descargar el contexto Git público. La referencia era correcta según el formato documentado, pero el repositorio consolidado no es accesible a un clonador anónimo. No se incluyeron credenciales GitHub en URL o logs.
- Se preparó una corrección que usa un paquete HTTPS inmutable del Engine, comprobado localmente con SHA-256 `cd0b8d20a7a0cc73e4862d25378f0aded45fb3bc0bbd60378e175af707483f2f`; ACR Tasks admite archivos tar remotos como contextos de build. El último despliegue federado con ese paquete se inició, pero el navegador que permite observación de Azure pasó a mostrar `There is no internet connection` antes de que se pudiera confirmar su resultado.
- Debido a que ni la CLI local ni el portal controlable ofrecen una sesión autenticada y observable en este momento, no se ejecutará otra actualización, no se iniciará el job IAAI y no se afirmará si el último build o actualización llegó a completarse. Copart permanece deshabilitado en Apibara por código y por la política del Engine.

## Cierre verificado mediante salida del Deployment Script y bridge

La salida posterior de `run-iaai-extended-afaac3d` confirmó que el paquete HTTPS se descargó, compiló y publicó correctamente. La imagen `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:iaai-extended-afaac3d` se publicó con digest `sha256:1757523fc52945d5cdae8c88df7dab1e4ce865b668b5e216165f17fef7cd1510`; la API se actualizó a `ca-lsc-inventory-api-prod--0000012`, `Running`, y el job IAAI quedó apuntando a la misma imagen. La salida confirma además `IAAI_JOB_EXECUTION_STARTED=false` y `COPART_APIBARA_ENABLED=false` durante el despliegue.

Después se inició una única ejecución manual del job IAAI. El bridge server-side confirmó 1,000 vehículos IAAI persistidos, sin Copart, y cobertura de campos ampliados: vendedor 1,000; daño secundario 427; documento de venta 848; motor 828; estimado proveedor 848; llaves y Buy Now 1,000; fotos 1,000; y URLs de media no HTTPS 0. La interfaz publicada cargó el corte de 1,000 lotes, 948 visibles por la política de títulos especiales, y redujo el resultado a 34 al aplicar el filtro de layout de motor `H3`. Una ficha publicada mostró fotos reales, documento, vendedor, motor, estimados, condición, trazabilidad y media. Las suites finales aprobaron 64 pruebas .NET y 51 pruebas UI.

## Evidencia r3 y bloqueo actual de activación del cron

- La captura del contenedor del job confirma la imagen `acrlscinvprodeus2.azurecr.io/lsc-inventory-engine:iaai-national-r3-full-backfill`, el comando `dotnet Lsc.Inventory.Api.dll` y la identidad administrada. La ejecución manual posterior finalizó correctamente y el bridge reportó 3,422 registros IAAI persistidos en el corte `Open`.
- Se preparó y validó la plantilla ARM de programación r2. Hace un PATCH exclusivamente del trigger del job existente a `Schedule`, con cron `*/30 * * * *`, timeout de 1,500 segundos, reintento 1, paralelismo 1 y completion count 1. Exige la imagen r3 y comprueba después que imagen, identidad, secretos y variables no cambiaron.
- Tras la autorización expresa del usuario, el portal de Azure autenticado abrió la plantilla r2, pero el selector de `Resource group` para la suscripción `LSC Inventory Feed Project` devolvió `No available items` y tampoco encontró `rg-lsc-inventory-prod` al filtrarlo. No se creó despliegue ni se cambió la programación. Se debe recuperar el selector o usar una ruta autenticada equivalente antes de afirmar que el cron está activo.

## Bloqueo de despliegue de búsqueda paginada r5

- El reporte protegido de validación del API confirmó 62,481 lotes persistidos. La ruta heredada `/api/v1/inventory/recent` responde, pero limita cualquier lectura a 1,000 vehículos. La nueva ruta `/api/v1/inventory/search` respondió `404` desde la revisión Azure actual, por lo que el portal no debe publicarse contra ese contrato hasta actualizar la API.
- El código r5 ya contiene conteo total, facetas y búsqueda paginada server-side. También existe un contexto de build inmutable y una plantilla ARM limitada a la API; el script comprueba que la imagen y trigger del job IAAI no cambien, no ejecuta el job, no activa cron y no habilita Copart mediante Apibara.
- El intento de acceder al job por URL directa en Azure Portal devolvió `InternalServerError` con Resource ID no disponible. Resource Explorer redirigió a inicio de sesión y la computadora en la nube no tiene Azure CLI instalada. Por lo tanto no existe una ruta autenticada verificable para aplicar r5 en esta sesión.

## Paquete final preparado para el despliegue conjunto

- La revisión final de API es `inventory-search-r5b-complete-filters`. El contexto de build inmutable está publicado en `/manus-storage/lsc-inventory-engine-r5b-complete-filters.tar_659e3884.gz` (SHA-256 `f4a0de18a4d893a6ba18ec34f9e58f5a2e65e2be6a04dfd47106329f874b5cb9`).
- La plantilla ARM inmutable es `/manus-storage/azure-template-inventory-search-api-r5b_8f9584f4.json`. Solo construye la imagen y actualiza `ca-lsc-inventory-api-prod`; comprueba que imagen y trigger de `job-lsc-iaai-pilot-prod` no cambien. El script declara explícitamente `IAAI_JOB_EXECUTION_STARTED=false`, `SCHEDULE_ENABLED=false` y `COPART_APIBARA_ENABLED=false`.
- La validación local final aprobó 68 pruebas del Engine y 37 pruebas UI/bridge. La validación de producción queda bloqueada hasta que se aplique el despliegue de API autenticado.
