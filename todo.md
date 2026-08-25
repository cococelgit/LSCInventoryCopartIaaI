- [x] Inspeccionar los payloads auditados para confirmar URLs y campos reales de media.
- [x] Renovar el acceso de lectura a registros de Azure con un código de dispositivo vigente.
- [x] Verificar que la cuenta desbloqueada ya permite consultar los registros de diagnóstico.
- [x] Definir el acceso temporal privado a imágenes y datos completos sin exponer la llave del proveedor.
- [x] Integrar galería real y ficha ampliada por lote en una nueva pestaña.
- [x] Verificar la interfaz, guardar versión y compartir la URL temporal actualizada.

- [x] Elegir la frecuencia de actualización del feed y el alcance de fotos en vivo.
- [x] Resolver la integración full-stack preservando el UI existente y los secretos server-side.
- [x] Conectar datos y media mediante API segura, con estados de carga y error.
- [x] Verificar actualizaciones, permisos, cuota del proveedor y publicar la nueva versión.

- [x] Diseñar el flujo operativo de inventario cacheado con actualización automática cada 30 minutos.
- [x] Medir consumo de cuota de Apibara por ejecución y definir límites por alcance.
- [x] Implementar el proceso automático y la lectura del UI desde el inventario persistido.
- [x] Validar frescura, fallos, duplicados, media y publicación temporal.

- [x] Verificar la cuota activa expuesta por Apibara: plan Test, límite efectivo 100, 75 solicitudes restantes; escalar el plan antes de agotar el límite.
- [x] Conectar el backend del UI al inventario persistido del motor sin exponer secretos.
- [x] Configurar una ejecución automática cada 30 minutos con reintentos controlados y estado de frescura.
- [x] Validar una corrida controlada, fotos, errores, cuota restante y publicación temporal.

- [x] Publicar una API Azure de lectura protegida como única fuente del UI.
- [x] Mantener PostgreSQL, Blob y la llave de Apibara fuera del alcance público.
- [x] Activar el job de sincronización automática cada 30 minutos con auditoría de uso bajo demanda protegida.
- [x] Conectar el UI a la API Azure y verificar frescura, fotos y estados de error.

- [x] Ejecutar la verificación y el despliegue directamente desde la sesión Azure autenticada, sin pedir al usuario copiar comandos.

- [x] Corregir el job programado y validar ejecuciones exitosas posteriores sin alteraciones administrativas de datos.

- [x] Actualizar en Key Vault la clave vigente de Apibara y restaurar de inmediato el acceso privado de la bóveda.
- [x] Repetir una corrida controlada después de validar la nueva credencial antes de considerar estable el cron de 30 minutos.
- [x] Corregir la API de lectura para no exigir Apibara al arrancar cuando la sincronización está desactivada.
- [x] Verificar la revisión Azure corregida, el puente tRPC, las fotos dinámicas y los estados de frescura.
- [x] Guardar un nuevo checkpoint del UI conectado a Azure y registrar la versión publicada: `84fa3973`.
- [x] Activar y validar la revisión de API que lee snapshots persistidos en PostgreSQL.
- [x] Verificar una respuesta de inventario con lotes y fotos reales tras la corrección de lectura.
- [x] Confirmar el bridge tRPC y la ficha de detalle en el UI con datos vivos.
- [x] Publicar y validar la versión final del UI conectado en su URL desplegada.
- [x] Ajustar el consumo del job a la cuota real expuesta por Apibara antes de mantener el cron de 30 minutos.
- [x] Mantener el alcance completo del feed y escalar el plan de Apibara cuando la cuota operativa lo requiera, conforme a la instrucción del usuario.
- [x] Eliminar los fallbacks de catálogo local del UI y mostrar un estado explícito cuando Azure no responda.
- [x] Definir y validar una auditoría de uso bajo demanda protegida que no multiplique llamadas en cada ejecución del cron.
- [x] Proteger los endpoints internos de validación, uso y sincronización con el token de servicio para impedir acceso o ejecuciones públicas.
- [x] Reintentar la publicación del UI tras el timeout de red del registro de imágenes y validar el despliegue resultante.
- [x] Revisar la carpeta oficial de recursos de LSC y seleccionar logo, variantes y colores de marca aprobados.
- [x] Sustituir la identidad visual provisional del Inventory UI por los activos oficiales de LSC.
- [x] Validar y publicar la actualización visual de marca en el dominio del Inventory UI.
- [x] Analizar la referencia de BidCars y definir una jerarquía de búsqueda adaptada a LSC sin copiar su identidad.
- [x] Reemplazar el hero y la consola de evidencia por una barra de búsqueda compacta y filtros completos fijos a la izquierda.
- [x] Rediseñar el listado derecho como resultados densos con foto real, datos clave, puja y enlace de ficha en nueva pestaña.
- [x] Validar filtros, datos vivos y comportamiento responsive del buscador simplificado.
- [x] Publicar y verificar el rediseño del buscador en el dominio de LSC.
- [x] Revisar y normalizar los valores disponibles de daño, título y transmisión desde el corte Azure: el corte actual reporta “No reportado” en los 57 lotes.
- [x] Añadir filtros laterales funcionales de tipo de daño, estado de título y transmisión, con opciones dinámicas basadas en el corte Azure.
- [x] Validar los nuevos filtros con datos vivos y publicar la mejora.
- [x] Inventariar los filtros relevantes de BidCars y contrastarlos con los campos reales de Azure.
- [x] Añadir filtros funcionales de odómetro, tipo de tracción y tipo de combustible.
- [x] Añadir los filtros adicionales soportados por el feed y estados honestos para los no disponibles.
- [x] Validar la barra lateral ampliada, filtros combinados y comportamiento responsive con el corte Azure.
- [x] Publicar y verificar la barra lateral ampliada en el dominio de LSC, incluidos filtros inferiores y una prueba directa de modelo en producción.
- [x] Auditar los campos de facility, estado, odómetro, daño, título, tracción y combustible en el contrato y payload del proveedor: facility/estado ya existen; el título se estaba descartando en la salida pública; los demás campos requieren lectura de detalle cuando no vengan en la lista.
- [x] Enriquecer la sincronización para persistir datos reales por lote sin aumentar llamadas innecesarias.
- [x] Corregir el mapeo público del estado de título y conservar facility/estado en el contrato del UI.
- [x] Agregar filtros activos de ubicación/facility y estado a la barra lateral.
- [x] Ejecutar una sincronización controlada, validar los campos enriquecidos y publicar el buscador actualizado.
- [x] Guardar la versión verificable del UI con filtros de facility/estado y el contrato actualizado.
- [x] Validar el dominio publicado con facility/estado y al menos un campo enriquecido real.
