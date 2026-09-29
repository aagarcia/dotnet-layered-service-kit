# 14 — Trampas en producción

Cada entrada es un fallo real que costó tiempo real. Lee la tabla completa antes de integrar (merge)
cualquier cosa que toque una escritura masiva, una cola, un SDK de terceros o un servicio en segundo plano.

**Síntoma → Causa → Corrección.** El tema de la mitad de estas es la misma lección: *la suite de pruebas
pasó y producción se rompió igual.* Sabe cuáles de tus pruebas te están mintiendo.

---

## 1. Un upsert masivo crea una tabla real en tu esquema

| | |
|---|---|
| **Síntoma** | La escritura masiva falla con un error de permisos que no menciona nada sobre permisos — o funciona en la máquina del desarrollador y falla con el login de la aplicación en el primer despliegue real. |
| **Causa** | `EFCore.BulkExtensions` sin `UseTempDB = true` crea una **tabla de staging con un nombre real en tu esquema** y la elimina, en cada lote. Eso necesita `CREATE TABLE` y `DROP TABLE` sobre el esquema destino. Un login de privilegios bajos no tiene ninguna de las dos. |
| **Corrección** | Siempre `new BulkConfig { UseTempDB = true }`. Hace el staging en una tabla temporal que se descarta junto con la conexión. |
| **Detección** | Un login que puede `SELECT`/`INSERT`/`UPDATE` pero no `CREATE TABLE`; cualquier cosa que solo aparece contra la base de datos real. |

```csharp
await db.BulkInsertOrUpdateAsync(rows, new BulkConfig { UpdateByProperties = [key], UseTempDB = true },
    cancellationToken: cancellationToken);
```

## 2. `UseTempDB = true` requiere una transacción explícita

| | |
|---|---|
| **Síntoma** | `InvalidOperationException` de la librería masiva, de inmediato, contra un SQL Server real. |
| **Causa** | El staging en tabla temporal tiene el alcance de la transacción. Sin ella no hay nada donde hacer el staging. |
| **Corrección** | Abre la transacción explícitamente alrededor de la llamada masiva. Es un requisito estricto, no una preferencia de estilo. |
| **Detección** | Solo contra un SQL Server real. SQLite no lo reproduce. |

## 3. Una estrategia de ejecución con reintento y una transacción iniciada a mano son excluyentes

| | |
|---|---|
| **Síntoma** | `InvalidOperationException` en el instante en que llamas a `BeginTransactionAsync()`: *"The configured execution strategy 'SqlServerRetryingExecutionStrategy' does not support user-initiated transactions. Use the execution strategy returned by 'DbContext.Database.CreateExecutionStrategy()' to execute all the operations in the transaction as a retriable unit."* |
| **Causa** | Con `EnableRetryOnFailure` configurado, el contexto usa una estrategia de ejecución con reintentos. Las transacciones iniciadas por el usuario fuera de esa estrategia se rechazan por diseño — si no, un reintento se ejecutaría en silencio fuera de la transacción. |
| **Corrección** | Deja que la estrategia sea la dueña de la transacción: `db.Database.CreateExecutionStrategy().ExecuteAsync(async () => { ... })`, y abre la transacción **dentro** de ella. |
| **Detección** | Solo cuando `EnableRetryOnFailure` está activo y solo contra un SQL Server real. |

```csharp
var strategy = db.Database.CreateExecutionStrategy();
await strategy.ExecuteAsync(async () =>
{
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    // ... escritura masiva, inserts de detalle, SaveChanges ...
    await transaction.CommitAsync(cancellationToken);
});
```

## 4. Ninguna de 1–3 se reproduce en SQLite

| | |
|---|---|
| **Síntoma** | 400 pruebas en verde, y el primer despliegue a producción falla en la primera escritura por lotes. |
| **Causa** | SQLite es un motor SQL real para consultas, pero no tiene ruta de staging masivo por `tempdb`, ni estrategia de ejecución de SQL Server, ni el mismo modelo de transacción. Los tres comportamientos más peligrosos de la capa de persistencia son exactamente los que la base de datos de pruebas no puede expresar. |
| **Corrección** | Mantén SQLite en memoria para las pruebas unitarias rápidas. Después **verifica contra un SQL Server real antes de publicar**: ejecuta el lote y comprueba que las filas realmente se mueven y que la transacción realmente hace commit. Trátalo como un paso manual de integración en `docs/DEPLOYMENT.md`. |

## 5. Un canal acotado puede perder datos en silencio

| | |
|---|---|
| **Síntoma** | Faltan registros y no hay error, ni línea de log, ni contador. Las filas de auditoría best-effort simplemente nunca aparecen. |
| **Causa** | Con `BoundedChannelFullMode.DropWrite`, `TryWrite` devuelve **`true` incluso cuando descartó el elemento**. El descarte es indistinguible del éxito. |
| **Corrección** | Usa `FullMode = BoundedChannelFullMode.Wait`. Entonces `TryWrite` devuelve `false` sin bloquear cuando el canal está lleno — y `false` es observable. |
| **Detección** | Compara la cuenta de entrada con la persistida bajo carga sostenida. El contador con un tag `reason` es la segunda línea de defensa. |

```csharp
public void TryEnqueue(AuditLogEntry entry)
{
    if (!channel.Writer.TryWrite(entry))
        OrderTelemetry.AuditErrors.Add(1, new KeyValuePair<string, object?>("reason", "channel_full"));
}
```

## 6. Un servicio con ámbito que envuelve estado compartido lo rompe

| | |
|---|---|
| **Síntoma** | El escritor no procesa nada. Sin errores, sin líneas de log, el canal está siempre vacío — y el productor «sabe» que escribió. |
| **Causa** | El wrapper alrededor de un canal singleton se registró como `Scoped`. Cada petición resuelve su propia instancia, así que el lector del escritor y el escritor del productor no son el mismo objeto, o el estado por petición se reinicia cada vez. |
| **Corrección** | Registra el wrapper como **Singleton** y comprueba que todo lo que lo consume también sea compatible con singleton. Un `BackgroundService` es singleton por naturaleza — todo lo que inyecta también debe serlo. |
| **Detección** | Registra el tipo concreto y resuelve la interfaz desde el mismo registro, para que ambos resuelvan la misma instancia. |

## 7. `CreateIfNotExistsAsync` por petición

| | |
|---|---|
| **Síntoma** | Las peticiones salientes a la cola se duplican, throttling bajo carga, latencia p95 subiendo mientras el payload sigue siendo pequeño. |
| **Causa** | `CreateIfNotExistsAsync` en cada envío es un viaje de ida y vuelta extra por mensaje. La cola casi siempre ya existe, así que es sobrecarga pura — y bajo carga se convierte en throttling. |
| **Corrección** | Crea la cola **una vez**: al arrancar en un `IHostedService`, o en el primer uso detrás de un `SemaphoreSlim` con un flag `volatile bool`. Nunca en el camino caliente. |
| **Detección** | Cuenta las llamadas HTTP al servicio de almacenamiento por mensaje ingerido. Debería ser una. |

## 8. Una columna con un `DEFAULT` de SQL se escribe como `0001-01-01`

| | |
|---|---|
| **Síntima** | Una columna de marca de tiempo está llena de `0001-01-01`, o las cuentas están mal porque el «default» nunca se aplicó. |
| **Causa** | EF envía el valor por defecto de CLR para todas las columnas del insert, y las librerías masivas incluyen todas las columnas. Una cláusula `DEFAULT` solo se dispara para un `INSERT` escrito a mano que omite la columna. |
| **Corrección** | Declárala en la configuración de la entidad: `ValueGeneratedOnAdd()` más `HasDefaultValueSql(...)` (o `HasDefaultValue(...)`). Fija el valor en código para el insert y no lo sobrescribas en el update. |
| **Detección** | Consulta el centinela `0001-01-01` en producción. Debería devolver cero filas. |

## 9. AOT y el trimming rompen los SDK basados en reflexión

| | |
|---|---|
| **Síntoma** | `System.NotSupportedException: Reflection-based serialization has been disabled`. Compila y publica sin problemas; lanza en la primera llamada que serializa. |
| **Causa** | `PublishAot`/`PublishTrimmed` elimina la maquinaria de reflexión de la que depende el SDK del proveedor. |
| **Corrección** | No actives AOT ni trimming en un servicio que use un SDK así. Si el objetivo de rendimiento lo exige, elige otro SDK o añade soporte explícito de `DynamicDependency`/generador de código fuente. |
| **Detección** | Solo en runtime, en la primera llamada al SDK. AOT no está cubierto por la suite de pruebas. |

## 10. Un SDK que mantiene sesión, construido por petición

| | |
|---|---|
| **Síntoma** | Agotamiento de sockets, logins duplicados, conexiones duplicadas contra el proveedor y, con el tiempo, throttling del lado del proveedor que parece tu propio bug. |
| **Causa** | El SDK mantiene una sesión y autentica bajo demanda. Construirlo por petición crea una sesión por petición. Pasarle un `HttpMessageHandler` de `IHttpClientFactory` es peor: el SDK lo retiene para siempre, así que nunca se rota nada. |
| **Corrección** | Una única instancia **singleton**, su propio `SocketsHttpHandler` con vida de proceso, y un `SemaphoreSlim` alrededor del primer login (el SDK no tiene candado de autenticación propio, así que N primeras peticiones concurrentes disparan N logins). |
| **Detección** | Cuenta los logins por cada N peticiones. Debería ser 1 por proceso, más uno por cada expiración de sesión. |

## 11. Parsear con la zona horaria del sistema operativo

| | |
|---|---|
| **Síntoma** | Las marcas de tiempo se desvían una hora — pero solo en un entorno. Dev está bien, producción está mal, o la misma entrada produce instantes distintos en regiones distintas. |
| **Causa** | `DateTime.Parse` / `DateTime.Now` usan la zona del host, que cambia con la región en la que corre el proceso y con las reglas de horario de verano. |
| **Corrección** | Una constante de **offset de negocio fijo**, usada en cada parseo y en cada conversión, con un comentario que diga de dónde sale el offset. Almacena en UTC; convierte solo para mostrar. |
| **Detección** | Cambia el `TZ` del contenedor a otro valor y vuelve a correr las pruebas. Cualquier cambio de comportamiento es el bug. |

## 12. Un nombre de clase que colisiona con un tipo de la BCL

| | |
|---|---|
| **Síntoma** | Un error de compilación confuso sobre un delegado que no coincide con la firma de un método, en una clase que parece correcta. |
| **Causa** | Un tipo nombrado como algo de la BCL — lo más común, `EventHandler` — colisiona con el namespace `System` importado implícitamente (`ImplicitUsings`) o con un tipo implícito. |
| **Corrección** | Renombra el tipo, o cualifícalo por completo en el sitio de uso. Renombrar es mejor: la colisión volverá la próxima vez que alguien quite la cualificación. |
| **Detección** | Haz grep de tus propios nombres de tipo que también existen en `System` antes de integrar. |

## 13. El CORS añadido «solo para pruebas» acaba en producción

| | |
|---|---|
| **Síntoma** | Una policy de CORS permisiva en una API servidor-a-servidor, añadida para un experimento local y revisada por nadie. |
| **Causa** | CORS es un mecanismo de navegador. Los llamadores servidor-a-servidor lo ignoran, así que parece gratis añadirlo «para las pruebas» — y entonces es lo único que se interpone entre la API y una superficie de ataque basada en navegador. |
| **Corrección** | Nada de CORS en una API servidor-a-servidor. Prueba con `WebApplicationFactory`, que no lo necesita. Si un navegador es un cliente real, usa una policy con nombre y una lista explícita de orígenes. |
| **Detección** | `grep -ri "AddCors\|UseCors" src/` no debería devolver nada en un servicio servidor-a-servidor. |

## 14. Un `appsettings.Development.json` versionado filtra credenciales

| | |
|---|---|
| **Síntoma** | Una cadena de conexión real, una clave de firma o una contraseña en el historial del repositorio. |
| **Causa** | El archivo está versionado por defecto en la mayoría de las plantillas, así que un solo `git add .` lo publica. Borrarlo después no deshace la filtración. |
| **Corrección** | Muévelo a gitignore. Usa `dotnet user-secrets` en local, variables inyectadas por la plataforma en la nube. **Rota cualquier cosa que se haya versionado** — quitar la línea no es remediación. |
| **Detección** | Analiza el historial, no solo el árbol actual, antes de hacer público un repositorio. |

## 15. Una cobertura que en silencio no produce nada

| | |
|---|---|
| **Síntoma** | `dotnet test --collect:"XPlat Code Coverage"` termina con éxito y no aparece ningún archivo de cobertura. |
| **Causa** | El proyecto corre sobre Microsoft.Testing.Platform, que no aloja extensiones de VSTest. `coverlet.collector` es un colector de VSTest: se carga, no hace nada y sale con 0. |
| **Corrección** | Usa `Microsoft.Testing.Extensions.CodeCoverage` y `dotnet test --coverage`. Quita la referencia vestigial a `coverlet.collector` para que nadie asuma que funciona. |
| **Detección** | Comprueba que el archivo de salida existe tras una ejecución de cobertura. |

## 16. Un proveedor nuevo no es una copia de uno existente

| | |
|---|---|
| **Síntoma** | La segunda integración hereda el flujo de la primera — su policy de reintento, su TTL de caché, su forma push/pull — y cada una de esas suposiciones es incorrecta para el proveedor nuevo. |
| **Causa** | El copy-paste le gana al diseño cuando la integración anterior está ahí mismo y ya funciona. |
| **Corrección** | Reutiliza la **estructura y las convenciones** — el layout de carpetas, el puerto, la forma del traductor de errores, los nombres de telemetría, las reglas de ciclo de vida. Diseña el **flujo** a partir de la naturaleza del proveedor: un proveedor push quiere un receptor, un proveedor solo-poll quiere un refresher, un proveedor REST stateless no quiere nada cacheado. Después descompila el SDK y lee sus límites reales. |
| **Detección** | Pregunta: «¿qué permite realmente la API de este proveedor?» Si la respuesta es una copia del diseño del otro proveedor, el diseño está mal. |

## 17. Un servicio en segundo plano que se cae se lleva todo el host

| | |
|---|---|
| **Síntoma** | Una falla transitoria - la base de datos queda inalcanzable un momento - tumba **toda la API**. El proceso termina, el orquestador lo reinicia, y las peticiones que estaban en vuelo fallan con un connection reset. Un `BackgroundService` que solo era un ayudante *best-effort* basta para provocar una caída completa. |
| **Causa** | Desde .NET 6 el default es `BackgroundServiceExceptionBehavior.StopHost`, así que una excepción que escapa de `ExecuteAsync` detiene **el host**, no solo ese servicio. Nada reinicia el servicio dentro del proceso, y el crash se le atribuye al host cuando la falla real está en un ayudante que nadie vigilaba. Solo es silencioso si alguien configuró explícitamente `HostOptions.BackgroundServiceExceptionBehavior = Ignore`. |
| **Corrección** | Captura dentro del bucle, cuenta el fallo y sigue: `ExecuteAsync` no debe dejar escapar ninguna excepción, porque el comportamiento por defecto convierte una sola en el apagado del host. El **callback del flush** es el único sitio que relanza, y allí debe loguear y contar **antes** de relanzar - los elementos ya salieron del buffer y el callback del timer no tiene a nadie más que avisarle. **No «arregles» esto cambiando el comportamiento a `Ignore`**: eso no hace la falla sobrevivible, solo deja de registrarla. |
| **Detección** | Alerta sobre el contador de errores del writer, y trata una línea de log `StopHost` como una caída completa, no como una advertencia de segundo plano. |

## 18. El apagado pierde el trabajo que estaba en el buffer

| | |
|---|---|
| **Síntoma** | Faltan las últimas cientos de filas de auditoría antes de un despliegue. Nunca durante el día, siempre en el apagado. |
| **Causa** | El timer de flush es periódico; un despliegue entre ticks descarta lo que haya en el buffer. |
| **Corrección** | Sobrescribe `StopAsync`, haz flush y luego llama a la implementación base. El `DisposeAsync` del acumulador debería hacer flush como su último acto. |
| **Detección** | Despliega en bucle y compara la última marca de tiempo antes del apagado con la hora del despliegue. |

## 19. Una cola sin límite

| | |
|---|---|
| **Síntoma** | La memoria sube hasta que el proceso se recicla, y entonces el backlog desaparece. |
| **Causa** | Un canal sin límite acepta todo, así que una base de datos lenta o un productor rápido se convierten en una condición de memoria agotada. |
| **Corrección** | `Channel.CreateBounded` con un tamaño elegido según el tráfico que puedes permitirte perder, y cuenta cada descarte. Un canal sin límite solo es aceptable cuando el productor está limitado por otra cosa. |
| **Detección** | Alerta sobre el contador `reason = channel_full`. Es un problema de capacidad del que te enteras en lugar de descubrirlo. |

## 20. `Task.Run` alrededor de código que ya es async

| | |
|---|---|
| **Síntoma** | Agotamiento del thread pool bajo carga, con mucha memoria libre. |
| **Causa** | Envolver trabajo `async` en `Task.Run` lo encola detrás del thread pool en vez de iniciarlo, añade un salto de planificación y consume un hilo del pool. También convierte en silencio la llamada en fire-and-forget cuando la tarea se descarta, perdiendo excepciones. |
| **Corrección** | Llama al método async y haz `await`. Usa `Task.Run` solo para trabajo realmente ligado a CPU que no tenga equivalente async. |
| **Detección** | `Task.Run` apareciendo en un camino de petición, o una tarea descartada. |

## 21. Llamar a `UseRateLimiter()` dos veces para tener dos límites

| | |
|---|---|
| **Síntoma** | Los límites configurados no son los efectivos. Uno puesto en 100/min rechaza a los ~50/min, y una policy por cliente que se escribió para ser justa acaba compartida por todos los llamadores. El comportamiento parece no determinista, y eso es la pista. |
| **Causa** | Cada `UseRateLimiter()` agrega otra instancia de `RateLimitingMiddleware`, y todas las instancias leen los *mismos* `RateLimiterOptions`: cobran el `GlobalLimiter` **y** la policy propia del endpoint. Así que dos llamadas cobran el `GlobalLimiter` dos veces por petición - el techo real es la mitad de lo que configuraste - y evalúan la policy basada en claims dos veces, la primera antes de `UseAuthentication()`, cuando `HttpContext.User` no tiene claims, así que todos los llamadores caen en la misma partición. La segunda llamada no «agrega» el límite por cliente: vuelve a correr los dos. |
| **Corrección** | Llama a `UseRateLimiter()` **una vez**, después de `UseAuthentication()` si alguna policy particiona por `azp`/`appid`. Para tener dos presupuestos, configura los dos niveles en los mismos options: un `options.GlobalLimiter` por IP más una policy de endpoint por client id, con ese único middleware cobrando ambos. La protección contra floods *antes* de validar el token es trabajo del borde (WAF / API gateway), no de un segundo middleware - la validación de JWT no hace I/O por petición porque las claves OIDC están cacheadas. |
| **Detección** | `grep -c "UseRateLimiter()" src/` devuelve más de 1; o un límite global observado en la mitad de su valor configurado. |

---

## Antes de integrar

Recorre esta lista explícitamente. No confíes en que has evitado las trampas por instinto.

- [ ] Ninguna escritura masiva sin `UseTempDB = true`, con una transacción explícita, envuelta en la
  estrategia de ejecución — y verificada contra un SQL Server real.
- [ ] Cada columna SQL `DEFAULT` tiene `ValueGeneratedOnAdd` / `HasDefaultValueSql`.
- [ ] Cada `Channel<T>` está acotado, con `FullMode = Wait`, y cada descarte se cuenta con un `reason`.
- [ ] Cada servicio que envuelve un canal, una caché o estado compartido es `Singleton`, y también todo
  lo que inyecta un `BackgroundService`.
- [ ] Ningún `CreateIfNotExistsAsync` en un camino de petición.
- [ ] Cada `BackgroundService` absorbe sus propias excepciones, las cuenta y hace flush en `StopAsync`; ninguno depende de `BackgroundServiceExceptionBehavior = Ignore` para seguir vivo.
- [ ] Cada SDK que mantiene sesión es un único singleton con su propio handler y un candado de login;
  AOT/trimming desactivados.
- [ ] Cada marca de tiempo se parsea con el offset de negocio fijo.
- [ ] La cobertura produce realmente un archivo.
- [ ] Ningún secreto en ningún `appsettings*.json` versionado; `appsettings.Development.json` en
  gitignore.
- [ ] Nada de CORS en una API servidor-a-servidor.
- [ ] `UseRateLimiter()` se llama exactamente una vez, colocado después de `UseAuthentication()` si alguna policy particiona por un claim.
- [ ] `dotnet build` y `dotnet test` limpios, cero advertencias.
