# 17 — Caché y Redis: cuándo usarlos, cuándo no, y dónde

Redis está **disponible como opción** en este kit. Nunca se agrega por defecto. El developer decide si
lo usa y dónde; tu trabajo es decir qué recomiendas y por qué, y avisarle con claridad cuando el lugar
que eligió no es viable. Usa este archivo junto con la reference 16, para todo endpoint donde el
developer mencione caché, Redis, velocidad, carga, o donde una lectura se repite.

## Paso 1 — ¿Se justifica siquiera una caché?

Una caché es una segunda copia del dato. Compra velocidad y cuesta corrección. No agregues una salvo
que los hechos lo indiquen.

| Hecho | Apunta a una caché | Apunta a no usarla |
|---|---|---|
| Costo de la lectura | Consulta cara, llamada lenta a un SDK externo, agregado pesado | Búsqueda indexada barata que la base de datos ya sirve bien |
| Proporción lectura/escritura | Se lee muchas veces por cada cambio | Se escribe casi tanto como se lee |
| Desactualización | El llamador acepta datos de segundos o minutos de antigüedad | El llamador debe ver la escritura de inmediato |
| Forma de la llave | Mismo resultado para muchos llamadores (un catálogo, una lista de referencia) | El resultado cambia por usuario o tenant y el espacio de llaves es enorme |
| Evidencia | Una medición muestra que la lectura es el cuello de botella | "Podría ser lento" sin números |

Si los hechos apuntan a no usarla, recomienda **sin caché** y di por qué. Es una respuesta válida y
común.

## Paso 2 — Qué tipo (lo más pequeño que funcione)

| Opción | Úsala cuando | Notas |
|---|---|---|
| **Sin caché** | Por defecto | Primero arregla la consulta, agrega un índice o una proyección (reference 16) |
| **En memoria, por instancia** (`IMemoryCache`, o `HybridCache` sin respaldo distribuido) | Una sola instancia, o la desactualización entre instancias es inofensiva | Lo más rápido, sin infraestructura nueva. Cada instancia guarda su copia |
| **`HybridCache` + Redis** (primer nivel en memoria, segundo nivel en Redis) | Varias instancias necesitan una copia compartida, o la copia debe sobrevivir un reinicio de la app (y Redis de verdad la retiene) | La protección contra stampede funciona **dentro de un proceso**: uno carga, los demás esperan. No es distribuida entre réplicas. Prefiérelo a un get/set armado a mano |
| **`IDistributedCache` sobre Redis directo** | Necesitas algo que `HybridCache` no da | `HybridCache` ya soporta serializadores propios y llaves definidas por el llamador, así que eso solo no lo justifica. Más código, sin protección contra stampede; justifícalo |
| **Output caching** (`AddOutputCache`) | Una respuesta HTTP completa es idéntica para todos los llamadores | Las respuestas autenticadas se excluyen por defecto. Variar por header o query no vuelve seguro cachearlas: sobreescribir ese default exige una política explícita y partición por identidad confiable |

Si Redis vale la pena es una **medición y un requisito**, no una regla. Varias instancias o una copia
compartida entre procesos son las razones habituales. Una sola instancia aún puede beneficiarse de
capacidad de memoria externa, pero con una instancia y sin medición Redis agrega un salto de red y un
modo de falla. "Sobrevive un reinicio" supone que Redis retiene el dato; no es una garantía de
durabilidad.

## Paso 3 — Dónde va (y dónde no)

- **Lecturas (`GET`):** cache-aside. Busca; si falla, carga desde la fuente y guarda con TTL.
- **Escrituras (`POST`/`PUT`/`PATCH`/`DELETE`):** nunca escribas en la caché como fuente de verdad.
  Invalida (elimina) las llaves afectadas **después del commit final** a la base de datos, nunca antes
  ni dentro de una transacción que aún no confirmó. Esto incluye `ExecuteUpdate`/`ExecuteDelete`
  (reference 16): se saltan el tracking, así que nada invalida por ti. Ver Paso 4, regla 10.
- **Ingesta asíncrona (`202`):** no se cachea. El endpoint encola (reference 08).
- **Llaves de idempotencia:** Redis puede guardar el registro de deduplicación, pero `IDistributedCache`
  no tiene set-if-absent atómico. Usa una API específica de Redis para un `SET key value NX EX|PX`
  atómico. **No** apliques a la deduplicación el comportamiento fail-open de una caché de lectura: si
  Redis está caído el endpoint debe decidir de forma explícita (rechazar, o recurrir a una restricción
  única en la base de datos). Un claim con expiración por sí solo no vuelve la operación de base de
  datos exactly-once; conserva la restricción única (reference 16).
- **No aquí:** la capa de dominio, la configuración de entidades, ni dentro de la lógica de negocio de
  un handler. La caché va **detrás de un puerto** como una shell de I/O delgada (reference 02): un
  decorator alrededor del lector, con la construcción de llaves en clases puras y testeables.

## Paso 4 — Reglas para lo que agregues

1. **Diseño de llaves.** Un prefijo de versión (`orders:v1:...`), cada componente escapado o con prefijo
   de longitud para que un separador dentro de un valor no haga que dos llamadores distintos compartan
   una llave, y un namespace de service/ambiente si Redis es compartido. Incluye el tenant o el usuario
   siempre que el resultado dependa de ellos, y toma esa identidad del **contexto del llamador
   autorizado**, nunca de un campo que el llamador controle. Una llave que omite una dimensión entrega
   a un llamador los datos de otro.
2. **TTL en cada entrada.** Ninguna entrada sin expiración. Agrega jitter para que muchas llaves no
   expiren juntas (las plantillas no lo hacen; es una adaptación requerida).
3. **Redis caído no debe tumbar la API.** `HybridCache` registra una falla de lectura de Redis (L2) y la
   trata como miss, y registra y traga una falla de escritura de L2. No agregues un `catch` amplio
   alrededor de `GetOrCreateAsync`: los llamadores concurrentes comparten una ejecución del factory,
   así que no puedes distinguir una falla de la caché de una falla de la fuente con una bandera local
   al llamador. Define timeouts cortos y explícitos para Redis y `AbortOnConnectFail = false`. Observa
   Redis con los logs y métricas del backend y de `HybridCache`.
4. **Autorización primero.** Nunca sirvas una respuesta cacheada antes de autorizar al llamador, ni
   cachees un resultado que dependa del llamador sin ese llamador en la llave.
5. **Sin secretos ni datos personales en los valores** salvo que deban estar ahí; Redis es
   infraestructura compartida.
6. **Acótala.** `MaximumPayloadBytes` limita un valor, no la memoria total. Acota también la cantidad
   de entradas (adaptación requerida); no cachees listas sin límite ni tablas completas.
7. **Obsérvala.** Cuenta las cargas de la fuente en el factory y las fallas de invalidación en un
   `Meter` registrado en `Domain/Telemetry` (reference 10). No infieras hit o miss de una bandera
   local al llamador. Una caché que nadie mide no se puede defender ni eliminar.
8. **Configúrala.** La cadena de conexión de Redis es un secreto. Las opciones usan `ValidateOnStart`
   (reference 11): TTL positivos, `LocalTtl <= DefaultTtl`, límite de payload positivo. Registra el
   backend de Redis solo cuando existe una cadena de conexión no vacía; registra siempre `HybridCache`.
9. **Pruébala.** Prueba unitaria del constructor de llaves (incluido un valor que contenga un
   separador) y de la lógica de invalidación con una caché falsa. Verifica los caminos de hit, miss y
   Redis caído contra una instancia real de Redis, no solo un fake (reference 12).
10. **La invalidación es de mejor esfuerzo tras el commit.** `RemoveAsync` puede lanzar una excepción
    después de que la base de datos ya confirmó; regístralo y cuéntalo, nunca falles la respuesta ni
    des a entender un rollback, y deja que el TTL acote la desactualización. Donde la desactualización
    no sea aceptable, agrega un reintento durable (outbox). Quitar una llave no limpia las copias en
    memoria de otras instancias (viven hasta `LocalTtl`), y una lectura ya en curso puede repoblar el
    valor viejo. La invalidación de todo un tenant necesita `RemoveByTagAsync` (expiración lógica, no
    borrado físico). Invalidar después del commit es el orden correcto; no es consistencia inmediata.

## Paso 5 — Dale un veredicto al developer antes de implementar

Agrega este bloque al plan de la reference 16 siempre que haya una caché sobre la mesa:

```
Caché:       <ninguna | en memoria | HybridCache + Redis | IDistributedCache + Redis | output cache>
Veredicto:   <Viable | Viable con condiciones | No recomendado>
Dónde:       <el endpoint/puerto que eligió el developer, o el lugar que recomiendas en su lugar>
Por qué:     <1-3 razones salidas de los hechos del Paso 1>
Llave + TTL: <forma de la llave incluyendo tenant/usuario, TTL, jitter>
Invalida:    <qué escrituras eliminan qué llaves, después del commit; política ante fallas>
Costo:       <dependencia nueva, Redis en el camino de falla, ventana de desactualización, tests extra>
Developer:   <la decisión es tuya; esta es la recomendación>
```

Si el developer la quiere en un lugar que calificas como **No recomendado**, dilo una sola vez, con la
razón y un lugar mejor, y luego implementa su elección con las reglas de arriba. La decisión es suya.

## Plantillas

Dos archivos listos para copiar, que se usan solo cuando el veredicto dice que una caché se justifica:

- `templates/CachingExtensions.cs` - `AddCaching(...)`: `HybridCache` siempre (en memoria), más Redis
  como segundo nivel solo cuando `Cache:RedisConnectionString` está definido. Opciones validadas con
  `ValidateOnStart`, timeouts cortos de Redis, `AbortOnConnectFail = false`. Sin cadena de conexión no
  hay Redis y el service arranca igual. Llámalo desde `Program.cs`, junto a `AddObservability`.
- `templates/CachedOrderStatusReader.cs` - un decorator detrás de un puerto, un constructor de llaves
  puro (componentes escapados, prefijo de versión, tag de tenant), conteo de cargas de la fuente en el
  factory, invalidación de mejor esfuerzo tras el commit, e invalidación de todo un tenant por tag.

Paquetes (proyecto Api), ver `references/15-checklists.md` (a2):

```bash
dotnet add src/Contoso.Orders.Api package Microsoft.Extensions.Caching.Hybrid
dotnet add src/Contoso.Orders.Api package Microsoft.Extensions.Caching.StackExchangeRedis   # solo para el segundo nivel en Redis
```

Configuración (la cadena de conexión es un secreto: `dotnet user-secrets`, nunca en el repo):

```json
"Cache": { "DefaultTtl": "00:05:00", "LocalTtl": "00:00:30", "MaximumPayloadBytes": 1048576, "RedisTimeout": "00:00:00.500" }
```

Agrega a tu clase de telemetría los contadores que usa el decorator (reference 03):
`CacheSourceLoads = Meter.CreateCounter<long>("cache.source.loads", description: "Cargas que fueron a la fuente porque la caché no pudo responder.")` y
`CacheInvalidationFailures = Meter.CreateCounter<long>("cache.invalidation.failures", description: "Invalidaciones de caché tras el commit que fallaron.")`.

No están implementados en las plantillas, así que son **pasos de adaptación requeridos**: jitter en el
TTL, un tope de cantidad de entradas, un reintento durable de invalidación y un namespace de
service/ambiente en las llaves.

Estado de verificación: las plantillas compilan contra `net10.0` con esos dos paquetes. **No** se han
ejecutado contra un Redis real; hazlo (hit, miss, Redis caído) antes de confiar en ellas (regla 9).

## Antipatrones

- Agregar Redis "por rendimiento" sin medición y con una sola instancia.
- Una llave sin el tenant o el usuario, o armada concatenando valores sin escapar.
- Tomar el tenant de la llave de un campo del request que el llamador controla.
- Invalidar antes del commit, o escribir en la caché como si fuera la base de datos.
- Hacer fallar la respuesta HTTP porque la invalidación posterior al commit lanzó una excepción.
- Sin TTL, o un solo TTL para todo.
- Redis como dependencia dura: la API da error cuando Redis está caído.
- Un `catch` amplio alrededor de `GetOrCreateAsync` que reintenta la fuente, o hit/miss contado desde
  una bandera local al llamador.
- Cachear con output caching respuestas HTTP autenticadas por usuario, sin política explícita.
- Un get-luego-set armado a mano, sin protección contra stampede, en una llave caliente.
- Cachear resultados de lógica de negocio dentro del dominio o del handler en vez de detrás de un
  puerto.
- Tratar "caché no disponible o aún sin cargar" como "no hay datos". Un resultado vacío o `null` de la
  fuente, cacheado legítimamente, es una respuesta válida; no los confundas (reference 09).
- Usar `IDistributedCache` para un claim de idempotencia que necesita un `SET ... NX` atómico.
