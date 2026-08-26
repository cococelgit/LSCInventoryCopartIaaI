# Estado de despliegue — 2026-08-26

## Código preparado

El monorepo contiene un Engine integrado que combina:

- Ingesta IAAI mediante Apibara, con mezcla lista/detalle y enriquecimiento controlado.
- Adaptador Copart Excel en streaming, separado de Apibara.
- Políticas determinísticas de limpieza, elegibilidad, auditoría y reconciliación.
- Contrato público ampliado para condición, documentos, vendedor, estimados y media IAAI.

La suite .NET combinada aprobó 64 pruebas. La suite UI aprobó 51 pruebas antes de la integración del adaptador, que no modifica el cliente.

## Producción actualmente servida

El bridge público responde 1,000 vehículos IAAI. La versión desplegada ya expone plataforma, color y Buy Now cuando se reportan, pero todavía no expone los campos extendidos del contrato nuevo, como vendedor, body style, motor, estimado del proveedor, llaves, daños primario/secundario ni nombre de documento.

## Bloqueo externo

El tenant Azure requiere reautorización por security defaults. Azure CLI no tiene una sesión activa y el navegador automatizado no puede completar el flujo de identidad empresarial. No se ha alterado la configuración productiva ni se ha reactivado Copart dentro de Apibara.

## Acción pendiente tras reautorización

Seguir `DEPLOY_IAAI_EXTENDED_RUNBOOK.md`: construir la imagen, actualizar API y job IAAI manual, ejecutar una sola sincronización enriquecida y validar campos desde el bridge LSC. Copart debe permanecer exclusivamente en el adaptador Excel.
