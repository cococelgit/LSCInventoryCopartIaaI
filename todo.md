- [x] Inspeccionar los payloads auditados para confirmar URLs y campos reales de media.
- [x] Renovar el acceso de lectura a registros de Azure con un código de dispositivo vigente.
- [x] Verificar que la cuenta desbloqueada ya permite consultar los registros de diagnóstico.
- [x] Definir el acceso temporal privado a imágenes y datos completos sin exponer la llave del proveedor.
- [x] Integrar galería real y ficha ampliada por lote en una nueva pestaña.
- [x] Verificar la interfaz, guardar versión y compartir la URL temporal actualizada.

- [x] Elegir la frecuencia de actualización del feed y el alcance de fotos en vivo.
- [x] Resolver la integración full-stack preservando el UI existente y los secretos server-side.
- [x] Conectar datos y media mediante API segura, con estados de carga y error.
- [ ] Verificar actualizaciones, permisos, cuota del proveedor y publicar la nueva versión.

- [x] Diseñar el flujo operativo de inventario cacheado con actualización automática cada 30 minutos.
- [x] Medir consumo de cuota de Apibara por ejecución y definir límites por alcance.
- [x] Implementar el proceso automático y la lectura del UI desde el inventario persistido.
- [ ] Validar frescura, fallos, duplicados, media y publicación temporal.

- [ ] Verificar con Apibara el plan/cuota real activa y actualizar la configuración para reflejar el límite efectivo antes de marcar resuelta la cuota de 1,500 solicitudes.
- [x] Conectar el backend del UI al inventario persistido del motor sin exponer secretos.
- [x] Configurar una ejecución automática cada 30 minutos con reintentos controlados y estado de frescura.
- [ ] Validar una corrida automática, fotos, errores, cuota restante y publicación temporal.

- [x] Publicar una API Azure de lectura protegida como única fuente del UI.
- [x] Mantener PostgreSQL, Blob y la llave de Apibara fuera del alcance público.
- [ ] Activar el job de sincronización automática cada 30 minutos con auditoría de uso.
- [x] Conectar el UI a la API Azure y verificar frescura, fotos y estados de error.

- [x] Ejecutar la verificación y el despliegue directamente desde la sesión Azure autenticada, sin pedir al usuario copiar comandos.

- [ ] Corregir el job programado para usar la imagen con migraciones administrativas separadas; la primera prueba falló sin modificar datos.

- [ ] Actualizar en Key Vault la clave de Apibara con la credencial vigente del plan de 1,500 solicitudes; la primera prueba automática devolvió 401 y emitió 0 solicitudes de datos.
- [x] Repetir una corrida controlada después de validar la nueva credencial antes de considerar estable el cron de 30 minutos.
- [x] Corregir la API de lectura para no exigir Apibara al arrancar cuando la sincronización está desactivada.
- [x] Verificar la revisión Azure corregida, el puente tRPC, las fotos dinámicas y los estados de frescura.
- [x] Guardar un nuevo checkpoint del UI conectado a Azure y registrar la versión publicada: `84fa3973`.
- [x] Activar y validar la revisión de API que lee snapshots persistidos en PostgreSQL.
- [x] Verificar una respuesta de inventario con lotes y fotos reales tras la corrección de lectura.
- [x] Confirmar el bridge tRPC y la ficha de detalle en el UI con datos vivos.
- [ ] Publicar y validar la versión final del UI conectado en su URL desplegada.
- [x] Ajustar el consumo del job a la cuota real expuesta por Apibara antes de mantener el cron de 30 minutos.
- [ ] Mantener el alcance completo del feed y escalar el plan de Apibara cuando la cuota operativa lo requiera.
- [x] Eliminar los fallbacks de catálogo local del UI y mostrar un estado explícito cuando Azure no responda.
- [ ] Definir y validar una auditoría de uso del proveedor que no multiplique llamadas en cada ejecución del cron.
- [x] Proteger los endpoints internos de validación, uso y sincronización con el token de servicio para impedir acceso o ejecuciones públicas.
- [ ] Reintentar la publicación del UI tras el timeout de red del registro de imágenes y validar el despliegue resultante.
