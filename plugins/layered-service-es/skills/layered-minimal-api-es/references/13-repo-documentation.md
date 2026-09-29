# 13 — Documentación del repositorio

## La regla

Cuatro documentos, cada uno con un trabajo distinto, y una regla sobre cómo se registran las decisiones:
**toda decisión no obvia lleva su *por qué*, en el mismo lugar donde vive la decisión.**

Por qué la documentación se pudre cuando solo registra *qué*: quien lee ya puede leer el código, así que
el único valor que aporta un documento es el razonamiento que ya no está en el código. Un archivo lleno de
firmas repetidas es peor que no tener archivo, porque parece autoritativo y está equivocado en un mes.

## `CLAUDE.md` — el manual de trabajo

El archivo más leído del repositorio. Orienta a un agente o a un desarrollador nuevo en menos de dos
minutos.

Debe contener, en este orden:

1. **Qué es esto** — un párrafo, y a qué capa pertenece cada proyecto.
2. **Comandos** — compilar, probar, cobertura, ejecutar en local, publicar. Pegables, no descripciones.
3. **La regla central** — la regla del shell de I/O delgado, con los pares concretos de *este*
   repositorio.
4. **Grafo de dependencias y DI** — qué proyecto referencia a cuál, y los tres puntos de registro.
5. **Decisiones no obvias** — cada una con su razón (ver más abajo).
6. **Convenciones** — las elecciones que quien lee tendría que inferir por su cuenta.

`docs/CLAUDE.md.template` en `templates/` es un punto de partida para rellenar huecos.

## `docs/ARCHITECTURE.md` — la forma y los flujos

- Un diagrama de componentes en ASCII: los llamadores, la API, las capas, cada sistema de terceros.
- El grafo de dependencias.
- **Ruta feliz**, paso a paso, nombrando las clases implicadas en cada paso.
- **Ruta de error**, como tabla: qué envió el llamador, qué pasó, qué código respondió y por qué ese
  código. Una fila por rechazo, incluidos los que el framework responde antes de que corra el handler.
- Una tabla de decisiones: `Decision | Why`.

## `docs/API-CONTRACT.md` — el contrato de cable

- Cada ruta: método, path, autenticación requerida, policy de rate limiting, esquema del request, esquema
  de la respuesta, cada código de estado que puede devolver.
- Modelo de error: la forma de ProblemDetails, el diccionario `errors` y el mensaje estable por código.
- El contrato de la cola: nombre, esquema del mensaje, versión, qué debe tolerar un consumidor.
- Regla de versionado: qué es aditivo y qué necesita una versión nueva.

## `docs/DEPLOYMENT.md` — cómo llega a producción

- Plataforma destino, comando de publicación, artefacto, pasos de zip/contenedor.
- Claves de configuración obligatorias y de dónde viene cada secreto — nombrados, nunca con su valor.
- Endpoints de health y readiness.
- Pasos de verificación posteriores al despliegue.

## `docs/TROUBLESHOOTING.md` — el índice de síntomas

Una entrada por cada síntoma que ha ocurrido de verdad, con la forma
`Symptom | Likely cause | How to confirm | Fix`. Escríbelo cuando depures algo, mientras todavía recuerdas
cómo se veía. Este es el archivo que se lee bajo presión.

## Registrar decisiones

Formato, siempre:

```markdown
- **Decisión en una línea.** Porque la razón que la impuso. Consecuencia si la olvidamos.
```

La tercera cláusula es lo que hace que una decisión se quede. Ejemplos de la forma:

```markdown
- **La normalización ocurre en la API, antes de encolar, no en el consumidor.** Porque cada consumidor
  tendría que reimplementar el mismo mapeo y, en el momento en que cambie el formato de origen, todos se
  rompen a la vez. Si lo mueves, el contrato de la cola cambia y los consumidores deben redesplegarse
  juntos.
```

```markdown
- **La zona horaria de negocio es un offset fijo UTC-05:00, nunca la zona del host.** Porque parsear con
  la zona del host hace que la misma entrada produzca un instante distinto en un portátil y en la región
  de producción. Si parseas con la zona local vas a publicar un informe corrido en una hora que solo
  aparece en producción.
```

```markdown
- **El rate limiting es por instancia, no distribuido.** Porque el limitador integrado es en proceso. Con N
  instancias el límite efectivo es N veces el valor configurado; si eso importa, los límites deben moverse
  a un almacén compartido.
```

Prueba cada entrada: *¿tomaría un desarrollador competente, leyendo solo el código, una decisión
distinta?* Si no, no pertenece al archivo.

## Qué no documentar

- Repeticiones de firmas. El código es la referencia.
- Un changelog. Usa el control de versiones.
- Arquitectura aspiracional. Si no está construida, no está en `ARCHITECTURE.md`.
- Secretos, cadenas de conexión, client ids, tenant ids, nombres de host internos.
- Los nombres del equipo o las URLs internas.

## Mantener la documentación viva

- Actualiza la documentación **en el mismo cambio** que altera el diseño. Una tarea posterior de
  «actualizar la documentación» es una tarea que nunca ocurre.
- Revisa la tabla de decisiones de `docs/ARCHITECTURE.md` en cualquier revisión de arquitectura; las
  decisiones obsoletas suelen ser decisiones que se revirtieron.
- El agente revisor (`agents/service-architecture-reviewer.md`) trata la ausencia de una entrada de
  decisión no obvia como un hallazgo. Es intencional: el razonamiento es la única parte que la
  documentación guarda de forma única.
