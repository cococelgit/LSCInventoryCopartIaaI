# Dirección de diseño — Inventory Review

## Tres enfoques explorados

### 1. Tablero de torre de control
**Muy breve:** Un panel oscuro de operación, con bloques de estado y señales de confianza que se sienten como una sala de mando de datos. Prioriza jerarquía, control y auditabilidad.
**Probabilidad:** 0.07

### 2. Bitácora editorial de subasta
**Muy breve:** Una estética clara, con acentos de papel, información de trazabilidad y tipografía de informe ejecutivo. Se sentiría como una hoja de evaluación de riesgo impresa y moderna.
**Probabilidad:** 0.04

### 3. Registro técnico automotriz
**Muy breve:** Una interfaz sobria de color petróleo, capas translúcidas y detalles inspirados en una ficha técnica. Conecta la precisión operativa con la categoría automotriz sin caer en un sitio de venta.
**Probabilidad:** 0.09

## Enfoque elegido: Tablero de torre de control

### Movimiento de diseño
**Operational intelligence / command center** aplicado a un producto B2B: información estructurada, contraste alto, líneas de señalización y datos vistos como evidencia, no como adorno.

### Principios centrales
1. **Evidencia antes que promesa:** cada módulo distingue hechos validados, límites del feed y controles activos.
2. **Jerarquía operativa:** el estado crítico aparece primero; el detalle queda disponible en capas inferiores.
3. **Disciplina visual:** pocos colores, tipografía técnica legible y componentes con bordes definidos, sin ornamento innecesario.
4. **Seguridad visible:** el usuario entiende de inmediato que es una vista privada, de solo lectura y sin acciones de compra.

### Filosofía de color
Base de azul petróleo casi negro para concentración y contraste. El verde jade se reserva para estados validados y el ámbar para advertencias o datos incompletos. Los grises azulados organizan las capas de información sin crear ruido.

### Paradigma de layout
Una barra lateral de control fija a la izquierda acompaña un flujo principal asimétrico. La cabecera es una franja de situación con una textura técnica; abajo, los indicadores se organizan como módulos de misión. No hay hero de marketing ni CTA de venta.

### Elementos distintivos
1. Una **línea de pulso** que recorre los módulos de estado y comunica actividad controlada.
2. Una **malla de coordenadas** tenue en los fondos de paneles, inspirada en trazabilidad y ubicación.
3. Sellos de estado monospace con punto de señal para “validado”, “manual” y “privado”.

### Filosofía de interacción
Las interacciones son de inspección: pestañas para cambiar entre resumen, calidad y lotes; tooltips y detalles de lectura. Ningún botón inicia compras, pujas ni sincronizaciones.

### Animación
Las tarjetas entran en una secuencia leve (40–60 ms de diferencia) usando solo opacidad y desplazamiento vertical mínimo. Los filtros y pestañas responden en menos de 180 ms. La animación se desactiva con `prefers-reduced-motion`.

### Sistema tipográfico
**Space Grotesk** para titulares, cifras y etiquetas de alto nivel; **Manrope** para texto de lectura. Las etiquetas operativas usan una variante monospace del sistema con mayúsculas y tracking amplio.

### Esencia de marca
**La Subasta Cubana Inventory Review:** una torre de control interna para comprobar qué inventario llegó, con qué calidad y bajo qué controles, antes de que lo use un asesor.

Personalidad: **rigurosa, directa, auditable**.

### Voz de marca
Los titulares declaran el estado; las notas explican el límite sin dramatizar. Los CTAs, si existen, solo invitan a inspeccionar datos, no a comprar.

Ejemplos: “Inventario validado, no prometido.” y “La ausencia de un campo se muestra; no se rellena.”

### Wordmark y logo
Un monograma abstracto de tres trazos que sugiere una “L” formada por carriles de subasta y una señal de verificación. Sin texto dentro del símbolo; el wordmark se compone tipográficamente junto al icono.

### Color de marca distintivo
**Jade de control — #32D6A0**, reservado para validación, progreso y señales de confianza.

## Style Decisions

- La barra lateral de control permanece persistente en escritorio y se convierte en panel deslizable en móvil; nunca se sustituye por la navegación superior.
- El monograma y el wordmark **La Subasta Cubana · Inventory Review** se muestran como firma del producto en la consola.
- La línea de pulso y los sellos monospace se repiten como lenguaje de trazabilidad entre estado, métricas y alcance del feed.
- La imagen secundaria se mantiene abstracta y basada en señales de datos, no en fotos que puedan leerse como catálogo o venta de vehículos.
- La exploración de inventario adopta una composición limpia de **filtros laterales + resultados a la derecha**, con fondo blanco, azul profundo y rojo de señal inspirados en la bandera cubana.
- Cada resultado abre una ficha de detalle en una pestaña nueva; los datos no entregados por el feed se muestran como ausentes y no se sustituyen con contenido inventado.
- La consola conserva una jerarquía de control: rail lateral persistente, estado del corte y sellos de auditoría antes de la lista. El jade `#32D6A0` comunica validación; el rojo queda reservado para geometría de marca y campos críticos ausentes.
