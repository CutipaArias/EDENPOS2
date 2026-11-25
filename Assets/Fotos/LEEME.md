# Fotos reales del catálogo

Ponga aquí fotos reales del local y de los artículos. Reemplazan los íconos genéricos (emojis de Twemoji) en las tarjetas de **Ventas**. Si un artículo no tiene foto, se sigue viendo su ícono.

Después de agregar fotos, compile de nuevo para que se copien junto al programa:

```powershell
dotnet build EdenPOS.csproj -c Release
```

## Cómo nombrar cada foto

Formatos: `.jpg`, `.jpeg`, `.png` o `.bmp` (no `.webp`). El nombre del archivo va en minúsculas, sin tildes ni eñes y con guiones en lugar de espacios.

La app busca la foto en este orden y usa la primera que encuentra:

1. **La foto elegida a mano** en Inventario → Editar artículo → Imagen. Esta siempre gana.
2. **Por nombre del artículo**: `alq-toalla.jpg` es solo para "ALQ TOALLA".
3. **Por tipo de ícono**: `towel.jpg` sirve para todos los artículos que usan el ícono de toalla (ALQ TOALLA y EXFOLIANTE SAUNA).
4. El ícono genérico.

| Categoría | Artículo | Foto solo para este artículo | O compartida por ícono |
|---|---|---|---|
| ALQUILERES | ALQ BIKINI MUJER | `alq-bikini-mujer.jpg` | `swimsuit.jpg` |
| ALQUILERES | ALQ FLOTADOR | `alq-flotador.jpg` | `float.jpg` |
| ALQUILERES | ALQ GORRO | `alq-gorro.jpg` | `cap.jpg` |
| ALQUILERES | ALQ LENTES | `alq-lentes.jpg` | `goggles.jpg` |
| ALQUILERES | ALQ SANDALIA | `alq-sandalia.jpg` | `sandal.jpg` |
| ALQUILERES | ALQ SHORT | `alq-short.jpg` | `shorts.jpg` |
| ALQUILERES | ALQ TOALLA | `alq-toalla.jpg` | `towel.jpg` |
| ALQUILERES | PAREO | `pareo.jpg` | `pareo.jpg` |
| ASEO & VENTAS | ACONDICIONADOR | `acondicionador.jpg` | `lotion.jpg` |
| ASEO & VENTAS | AFEITADOR | `afeitador.jpg` | `razor.jpg` |
| ASEO & VENTAS | BICARBONATO | `bicarbonato.jpg` | `salt.jpg` |
| ASEO & VENTAS | BLOQUEADOR | `bloqueador.jpg` | `sun.jpg` |
| ASEO & VENTAS | DESODORANTE | `desodorante.jpg` | `spray.jpg` |
| ASEO & VENTAS | ESPONJA DE BAÑO | `esponja-de-bano.jpg` | `sponge.jpg` |
| ASEO & VENTAS | EXFOLIANTE SAUNA | `exfoliante-sauna.jpg` | `towel.jpg` |
| ASEO & VENTAS | GANCHOS CABELLO | `ganchos-cabello.jpg` | `hair.jpg` |
| ASEO & VENTAS | GORROS | `gorros.jpg` | `cap.jpg` |
| ASEO & VENTAS | JABONES | `jabones.jpg` | `soap.jpg` |
| ASEO & VENTAS | LENTES | `lentes.jpg` | `glasses.jpg` |
| ASEO & VENTAS | MANO SAUNA | `mano-sauna.jpg` | `glove.jpg` |
| ASEO & VENTAS | SHAMPOOS | `shampoos.jpg` | `shampoo.jpg` |
| CAFETERÍA | GASEOSAS | `gaseosas.jpg` | `soda.jpg` |
| CAFETERÍA | HIDRATANTE | `hidratante.jpg` | `water.jpg` |
| CAFETERÍA | JARRAS | `jarras.jpg` | `pitcher.jpg` |
| CAFETERÍA | MARCIANOS | `marcianos.jpg` | `alien.jpg` |
| CAFETERÍA | SANDWICHES | `sandwiches.jpg` | `sandwich.jpg` |
| CAFETERÍA | SNACKS | `snacks.jpg` | `cookie.jpg` |
| SAUNA | ADULTO MAYOR | `adulto-mayor.jpg` | `elder.jpg` |
| SAUNA | AMIGOS X3 / X4 / X5 | `amigos-x3.jpg`, `amigos-x4.jpg`, `amigos-x5.jpg` | `friends.jpg` |
| SAUNA | FAMILIA 1 / 2 | `familia-1.jpg`, `familia-2.jpg` | `family.jpg` |
| SAUNA | GENERAL | `general.jpg` | `sauna.jpg` |
| SAUNA | MUJER 2 / 3 / 4 | `mujer-2.jpg`, `mujer-3.jpg`, `mujer-4.jpg` | `woman.jpg` |
| SAUNA | NIÑO | `nino.jpg` | `child.jpg` |
| SAUNA | PAREJA | `pareja.jpg` | `couple.jpg` |
| SAUNA | PASE CORTESÍA | `pase-cortesia.jpg` | `gift.jpg` |
| SAUNA | PRÉSTAMO GORRO | `prestamo-gorro.jpg` | `cap.jpg` |
| SAUNA | VIP | `vip.jpg` | `crown.jpg` |

Para los artículos que se creen después, aplique la misma regla al nombre: "AGUA MINERAL" → `agua-mineral.jpg`.

## Logo del local

`logo.png` (o `.jpg`) reemplaza el logo de la pantalla de inicio de sesión. Se muestra a 80 × 80, así que conviene un logo cuadrado de al menos 240 × 240 px con fondo blanco o transparente.

## Recomendaciones para las fotos

- **Formato**: horizontal (4:3 o 16:10). La tarjeta muestra la foto a todo el ancho y recorta lo que sobra por arriba y por abajo, así que deje el artículo centrado.
- **Tamaño**: unos 600 × 400 px y menos de 300 KB. Las fotos muy grandes hacen más lenta la pantalla de Ventas.
- **Fondo**: liso y claro, con buena luz. Que todas las fotos tengan el mismo estilo y la misma luz; así la pantalla se ve ordenada.
- **Servicios del sauna** (GENERAL, PAREJA, FAMILIA, etc.): una foto del ambiente (la sala de vapor, la piscina, el vestidor) funciona mejor que fotos de personas. Si aparecen clientes, pida su permiso.
