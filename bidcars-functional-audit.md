# Auditoría funcional de BidCars

Fuente revisada: [BidCars](https://bid.cars/en/), 26 de agosto de 2026.

## Navegación observada

El encabezado incluye selector de inventario Current, búsqueda global por marca/modelo/lote/VIN, acceso a Search & Bid, Live Auctions y autenticación. El menú Search & Bid expone accesos rápidos por Search, Featured, Estimated cost, Engine size/type/horsepower, Body style, Popular makes, Popular models, Loss type, Motorcycles and ATV y Personal Watercraft.

## Buscador compacto del home

| Grupo | Controles observados |
|---|---|
| Categoría | Automobile, Motorcycle, ATV, More |
| Estado | Archived |
| Vehículo | Make, Model, Generation, Year from, Year to |
| Identificador | VIN o lot number |
| Fuente | Copart, IAAI |
| Acción | Conteo dinámico y botón Show vehicles |

## Formato de tarjetas observado

Las tarjetas muestran fotografía dominante, año/marca/modelo, badge de fuente, estado de subasta y cuenta regresiva. La página inicial usa colecciones horizontales; todavía falta auditar el listado de resultados completo y una ficha real.

## Filtros del listado completo

La página de resultados ofrece un sidebar con los siguientes grupos funcionales:[2]

| Grupo | Controles y valores principales |
|---|---|
| Estado de subasta | All, Opened Auction, Live, Finished Today, Fast Buy, Archived Auctions |
| Precio | Estimated price, rango USD |
| Fuente | All, Copart, IAAI |
| Odómetro | Rango 0–250,000 mi |
| Start code | All, Stationary/No information, Vehicle starts, Run and Drive |
| Tracción | Front wheel drive, Rear wheel drive, All wheel drive |
| Transmisión | Automatic, Manual |
| Body Style | Sedan, SUV, Coupe, Pickup y expansión See more |
| Combustible | Gasoline, Diesel, Hybrid, Electric, Other |
| Loss Type | Mechanical, Hail, Fire, Water, Theft, Repossession y expansión See more |
| Color exterior | Black, White, Silver y expansión See more |
| Motor | Rango de litros 0–10; tipo Inline/V/W/Boxer; cilindros |
| Potencia | Rango 0–1,000 HP |

La auditoría con navegador conectado confirmó además que el selector de orden ofrece: Auction date asc/desc, Estimated Price asc/desc, Prebid price asc/desc, Buy Now price asc/desc, Year asc/desc y Odometer asc/desc.

## Formato de tarjeta/listado

Cada resultado incorpora carrusel de cinco fotos, título año/marca/modelo, fuente, estado de subasta, cuenta regresiva y precio actual/final. Cuando existe, también muestra Buy Now.[1]

La tarjeta de escritorio se organiza en tres zonas: foto/carrusel a la izquierda, identidad y condición al centro, y fecha/precio/estado a la derecha. Los campos visibles confirmados son título, VIN, lote, fuente, llave, transmisión, tracción, motor, cilindros, HP, odómetro en millas/km, vendedor, documento, ubicación, daño, start code, estimado low/high, fecha, current/final bid y estado Live/Finished. Badges adicionales identifican Video y Spin/360.

El sidebar usa filtros verticales persistentes; el listado coloca tabs de estado y orden arriba. La página muestra marcas como navegación secundaria independiente del panel de filtros. Para LSC se conservará la densidad funcional, pero no ese header comercial ni la navegación de marcas duplicada.

## Ficha auditada

Se auditó una ficha pública IAAI de 1989 Jaguar XJS para identificar la jerarquía informativa, no para copiar datos o textos.[3]

| Sección | Campos observados |
|---|---|
| Resumen superior | Start code, key, transmisión, combustible, tracción, motor y odómetro |
| Identidad | Año/marca/modelo, VIN, lote, fuente y enlace de origen |
| Venta | Facility/location, shipping origin, seller, sale document, fecha/hora y estado |
| Galería | Fotos completas, thumbnails, contador, video cuando existe |
| Condición | Loss, primary damage, secondary damage, odometer, start code, key, ACV/ERC |
| Especificaciones | Body style, color, engine, transmission, fuel, drive, manufactured in, class, cylinders y restraint system |
| Precio | Current bid, fast-buy/buy-now, tiempo restante y estimador desglosado |
| Contexto | Sales history, similares, títulos/documentos, notas de vendedor y comparación de subastas |

Para LSC, los cálculos europeos de shipping/customs y textos comerciales propios de BidCars no se replicarán. Se priorizarán los datos reales de subasta, el presupuesto LSC permitido y advertencias transparentes.

## Límite de réplica

La implementación LSC buscará paridad de información, filtros, densidad e interacción, pero conservará identidad, textos y código propios.

## Limitación responsive documentada

La página de escritorio y sus controles fueron auditados en el navegador conectado. La automatización headless móvil activó la verificación Cloudflare; por tanto, no se atribuyen a BidCars comportamientos móviles no observados. LSC implementará el mismo conjunto funcional dentro de su drawer móvil existente, con controles táctiles y tarjetas apiladas verificadas mediante pruebas propias.

## Referencias

[1]: https://bid.cars/en/iaai "BidCars IAAI inventory"
[2]: https://bid.cars/en/search/results?search-type=filters&status=All&type=Automobile&make=All&model=All&year-from=1900&year-to=2027&auction-type=All "BidCars search results"
[3]: https://bid.cars/en/lot/0-45839677/1989-Jaguar-XJS-SAJNV4841KC157397 "BidCars public IAAI lot detail"
