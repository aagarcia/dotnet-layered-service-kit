# 16 — Elegir el enfoque para cualquier endpoint (EF Core, SQL crudo / Dapper, cola, CQRS)

Usa este archivo **para todo endpoint que pida el developer, sin importar el verbo**: `GET`, `POST`,
`PUT`, `PATCH`, `DELETE` u otro. No elijas en silencio: antes de escribir código, dile al developer qué
vas a hacer, qué enfoque elegiste, por qué y qué descartaste. Luego implementa.

## Paso 1 — Clasifica la operación

| Operación | Qué hay que decidir |
|---|---|
| `GET` (uno / lista / reporte) | Cómo leer: proyección con EF Core, SQL crudo o un modelo de lectura aparte |
| `POST` crear | Insert simple, insert por lotes, o ninguna escritura en base de datos (encolar y responder `202`) |
| `POST` comando / acción | Un cambio de estado que no es un insert simple: alcance de la transacción, idempotencia, efectos secundarios |
| `PUT` | Reemplazo total: cargar y asignar, o update por conjuntos; concurrencia |
| `PATCH` | Actualización parcial: qué campos, cargar y modificar, o update por conjuntos; concurrencia |
| `DELETE` | Borrado físico o lógico; por conjuntos o por entidad; idempotencia |
| `HEAD`, `OPTIONS`, otros | Normalmente no hay nada que decidir; dilo y sigue |

## Paso 2 — Reúne los hechos (pregunta como máximo dos cosas; si no, declara tus supuestos)

| Hecho | Por qué decide |
|---|---|
| Forma: una fila, una página, una proyección con joins, un agregado, una raíz de agregado con hijos | Las formas simples son la fortaleza de EF Core; los joins pesados y agregados son candidatos a SQL crudo, pero solo una medición prueba que EF los traduce mal |
| Volumen y frecuencia: filas por llamada, llamadas por segundo, latencia objetivo | Solo una ruta medida o claramente grande justifica salirse del default |
| ¿El llamador debe ver el resultado de inmediato, o el trabajo puede ocurrir después? | Si puede esperar, el endpoint puede limitarse a encolar (reference 08) |
| Transacción y consistencia: una tabla o varias, qué debe confirmarse junto | Un `SaveChanges` relacional es atómico por defecto. Usa una transacción explícita cuando varias llamadas o comandos deban confirmarse juntos, envuelta en la execution strategy configurada (reference 04) |
| Concurrencia: ¿dos clientes pueden actualizar la misma fila? ¿Reintentos del cliente? | Decide un token de concurrencia y una llave de idempotencia |
| ¿Mismas tablas y modelo en el otro lado (lecturas vs escrituras)? | La divergencia es el umbral de este kit para separar proyecciones o almacenes de lectura |
| Motor de base de datos y funciones necesarias (window functions, CTEs, JSON, full-text) | EF Core puede no traducirlas bien |
| ¿La lectura se repite, es lenta o se reparte entre varias instancias? ¿El developer mencionó caché o Redis? | Abre la reference 17 y da un veredicto de caché; por defecto no hay caché |

## Paso 3 — Aplica la decisión

### Lecturas (`GET`)

**Por defecto: EF Core, solo lectura.** Para búsquedas por clave, listas filtradas o paginadas, joins
pequeños.

- `AsNoTracking()` y proyecta al DTO con `Select(...)`; nunca devuelvas entidades.
- Pagina siempre las listas (`Skip`/`Take` con un `OrderBy` estable, keyset en tablas grandes).
- Por qué: una herramienta y una configuración; la consulta está escrita a mano en el `Context` del
  módulo y se puede revisar (reference 04).

**SQL crudo cuando la lectura tiene forma de SQL:** joins de varias tablas con agregados, window
functions, CTEs, reportes, o una ruta donde **mediste** que la traducción de EF Core es el cuello de
botella.

- Prueba primero `Database.SqlQuery<T>($"...{valor}...")`: soporta tipos de resultado sin mapear y
  parametriza los **valores** interpolados, sin dependencia nueva y con la misma conexión. Los
  identificadores (nombres de tabla y columna) no pueden ser parámetros; usa una lista permitida para
  cualquier identificador dinámico. El SQL crudo debe aplicar por sí mismo los predicados de tenant y
  de borrado lógico.
- Agrega Dapper solo para lo que eso no da (multi-mapping, varios result sets, una conexión fuera de
  EF), y dilo en la decisión.
- Sigue siendo una shell de I/O delgada detrás de un puerto (reference 02); pruébalo contra el motor
  real (reference 12).

### Escrituras (`POST`, `PUT`, `PATCH`, `DELETE`)

**Por defecto: EF Core con change tracking**, un `SaveChanges` por request. Úsalo para escrituras de
una entidad o un agregado pequeño, donde las reglas de dominio necesitan la entidad cargada.

- **`POST` crear:** `Add` + `SaveChanges`; apóyate en una restricción única, no solo en una
  verificación previa; responde `201` con `Location`. Si los clientes reintentan, usa una llave de
  idempotencia e impleméntala bien: deduplicación atómica, huella de la solicitud, resultado guardado
  y un período de retención. Aceptar solo el header no es idempotencia.
- **`POST` recibir-y-procesar** (un partner envía datos para procesarse después): **no** escribas las
  tablas de negocio en el request. Valida, normaliza, encola y responde `202` (reference 08).
- **`POST` por lotes:** los lotes pequeños pueden usar `AddRange` + `SaveChanges`. Reserva
  `EFCore.BulkExtensions` para volúmenes que lo justifiquen, con las cuatro reglas de la reference 04,
  probado contra el motor real (reference 12), no solo SQLite.
- **`PUT` (reemplazo total):** carga, asigna todos los campos de reemplazo que el cliente puede
  escribir, `SaveChanges`; o un solo `ExecuteUpdate` cuando no hay reglas de dominio. Idempotente por
  definición (las respuestas no tienen que ser idénticas). Usa un token de concurrencia (`rowversion`
  es específico de SQL Server; otros motores tienen el suyo) y mapea un `If-Match` fallido a `412`, o
  un conflicto de aplicación sin precondición HTTP a `409`, con `ProblemDetails`.
- **`PATCH` (parcial):** cambia solo los campos enviados, distingue un campo omitido de un `null`
  explícito y usa una lista permitida de propiedades escribibles. Un `ExecuteUpdate` por conjuntos para
  cambios simples de columnas sin reglas de dominio (sin cargar); cargar y modificar cuando hay
  reglas. No agregues JSON Patch salvo que el cliente de verdad lo necesite. Mismo token de
  concurrencia que `PUT`.
- **Salvedades de `ExecuteUpdate` / `ExecuteDelete`:** se ejecutan de inmediato, se saltan el change
  tracking y los interceptores de `SaveChanges`, y no hacen cumplir los tokens de concurrencia por sí
  solos. Pon en el `Where` el token que el cliente leyó antes (un `rowversion` recién cargado no
  protege contra una edición vieja del cliente), revisa el conteo de filas afectadas y maneja el caso
  de cero filas de forma explícita. Las entidades rastreadas en el contexto quedan desactualizadas, y
  cada llamada es su propio viaje a la base de datos.
- **`DELETE`:** decide borrado físico o lógico y escribe la razón. Auditoría, retención y llaves
  foráneas piden una política de borrado explícita, que no siempre es borrado lógico. Borrado lógico
  significa asignar `IsDeleted` tú mismo; un query filter global solo oculta las filas marcadas y no
  convierte un delete en un update. `ExecuteDelete` borra físicamente, incluso con ese filtro
  configurado. Hazlo idempotente y documenta si una repetición responde `204` o `404`.

**SQL crudo / Dapper para escrituras** solo para una operación por conjuntos que EF Core no puede
expresar, o un stored procedure existente, usando primero `ExecuteSql`/`ExecuteSqlInterpolated`
(comandos sin resultado, misma parametrización de valores). Como política del kit, evita dos
escritores compitiendo sobre la misma tabla: las rutas de persistencia coordinadas están bien, la
propiedad en competencia no. Nunca Dapper para escribir "porque es más rápido" sin medición.

### CQRS — solo cuando los dos lados de verdad divergen

Elige CQRS completo (modelo/almacén de lectura aparte, con proyecciones propias) solo si se cumple al
menos una: las lecturas necesitan un almacén desnormalizado o distinto; o lectura y escritura escalan
distinto. Event sourcing y aceptar consistencia eventual son consideraciones, no razones suficientes
por sí solas, y CQRS puede compartir una sola base de datos.

- Tener `GET` y `POST` **no** es una razón. Los handlers por funcionalidad ya separan comandos de
  consultas en el código (reference 05).
- Este kit no adopta una librería de mediador (MediatR o similar); los handlers se llaman por interfaz.
- CQRS agrega un segundo modelo que mantener sincronizado. Declara ese costo.

## Paso 4 — Dile al developer el plan antes de implementar

Responde con este bloque. Espera un veto solo cuando la elección no es la de por defecto.

```
Operación: <VERBO /ruta> - <qué hace>
Plan:      <capas y archivos que tocarás: endpoint, handler, validador, puerto, Context, tests>
Contrato:  <códigos de estado: éxito y errores, casos de ProblemDetails>
Decisión:  <EF Core | EF Core ExecuteUpdate/ExecuteDelete | BulkExtensions | encolar + 202 | SqlQuery<T> | Dapper | CQRS>
Por qué:   <1-3 razones atadas a los hechos>
Descartado: <las otras opciones, una línea cada una, por qué no>
Costo:     <dependencia nueva, modelo extra, token de concurrencia, prueba contra el motor real, ...>
Caché:     <solo si aplica: veredicto y dónde, según la reference 17; si no, "ninguna">
Supuesto:  <hechos no confirmados, p. ej. "menos de ~10k filas, sin latencia objetivo, sin updates concurrentes">
```

Si el developer trae números que contradicen un supuesto, revisa la decisión. Una afirmación de
rendimiento sin medición es un supuesto; márcala como tal.

## Antipatrones

- Elegir el enfoque en silencio, de modo que nadie pueda revisar por qué.
- Dapper "porque es más rápido" sin medición.
- Devolver entidades de EF desde un endpoint, o una consulta con tracking en una lectura.
- Cargar una entidad solo para cambiar una columna cuando basta un `ExecuteUpdate` por conjuntos, o
  lo contrario: `ExecuteUpdate` donde las reglas de dominio necesitaban la entidad.
- `ExecuteUpdate`/`ExecuteDelete` sin token de concurrencia en el `Where` y sin revisar las filas
  afectadas.
- `PUT`/`PATCH` sin token de concurrencia sobre datos que dos clientes pueden editar.
- Un header de idempotencia que se acepta pero no se deduplica.
- Borrado físico de filas auditadas o referenciadas sin una política de borrado explícita.
- Escribir tablas de negocio dentro de un endpoint de ingesta que solo debería encolar.
- Un `IRepository<T>` genérico, o un mediador agregado "por CQRS" en un service con operaciones simples.
- Dos librerías de acceso a datos en competencia por la propiedad de las escrituras de la misma tabla
  (política del kit).
- SQL armado por concatenación de strings.
