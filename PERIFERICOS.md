# Módulo de pinpad e impresora

Este proyecto genera `SmartOne.Perifericos.dll` para servicios Windows de
.NET Framework 4.8. El ensamblado expone el cobro Niubiz/Ingenico mediante PCL
y la impresión de comprobantes ESC/POS por la cola de impresión de Windows.
También incluye un host HTTP local, `SmartOne.Perifericos.Host.exe`, para que
FastAPI solicite cobros en el pinpad y comprobantes en la impresora conectados
a esta misma máquina.

## Integración

Agrega una referencia a `SmartOne.Perifericos.dll` desde el servicio consumidor.
El proceso consumidor debe ejecutarse como x64 en Windows y tener junto a su
ejecutable las DLL nativas PCL distribuidas en `nativas`.

### Cobro con pinpad

```csharp
using PuntoVenta.Models.Pinpad;
using PuntoVenta.Services;

var pinpad = new PinpadService();
pinpad.Conectar();
CobroTarjetaResultado resultado = await pinpad.CobrarAsync(25.50m);
if (!resultado.Aprobado)
    throw new InvalidOperationException(resultado.MensajeError);
```

`CobroTarjetaResultado` conserva los datos del voucher y la respuesta cruda del
equipo. El servicio consumidor decide cómo registrar y presentar el resultado.

#### Conexión con el equipo

El pinpad se conecta por USB y Windows lo expone como puerto serie (en la caja
de referencia, `COM3`, con nombre `LANE3000-04224560`). El servicio PCL no
arranca solo: `AsegurarServicioEnSegundoPlano()` levanta la sesión en segundo
plano, y es lo que hace `SmartOne.Perifericos.Host.exe` al arrancar. Si no hay
pinpad, sólo queda anotado en `pinpad.log` y el módulo sigue cargando.

En una caja nueva hay un paso extra: PCL guarda el pareo del cable USB como
configuración **por máquina**. Hasta que no se parea, el equipo aparece
enchufado en `ListarUsb()` (`Activado = false`) pero `serverStatus` no reporta
canal y ninguna transacción responde. No hace falta hacerlo a mano:
`AsegurarServicioEnSegundoPlano()` activa el primer equipo visible sin parear.
El pareo se puede forzar por separado:

```csharp
var pinpad = new PinpadService();
pinpad.Conectar();
pinpad.AsegurarEquipoPareado();   // parea el primer equipo visible sin parear
// o, indicando el puerto concreto:
pinpad.ActivarUsb("COM3");
```

Para saber si el enlace está realmente vivo, `EstaConectado()` es un primer
filtro, pero la prueba definitiva es `LeerSerieDelEquipo()`: pide el número de
serie por el canal PCL y sólo el equipo puede contestarlo. `ProbarEcoAsync()`
va un paso más allá y lanza una transacción real contra la app de pago sin
cobrar nada (operación 20), así que sirve para verificar el circuito completo
sin tarjeta.

`serverStatus` no reporta canal al instante: tras `Conectar()` puede tardar
uno o dos segundos en dar el enlace por bueno. Por eso `Conectar()` es
idempotente — si vuelve a llamarse con la sesión ya arriba, `startPclService`
devuelve `false` y el módulo espera al canal antes de darlo por caído.

### Host local para FastAPI

Compila `Host\SmartOne.Perifericos.Host.csproj` en Release x64 y ejecuta
`SmartOne.Perifericos.Host.exe` en la misma PC que ejecuta FastAPI. Escucha
únicamente en `127.0.0.1:5005` y sólo acepta `POST` con el encabezado
`X-Peripheral-Token`:

| Ruta                                | Para qué                          | `Idempotency-Key` |
| ----------------------------------- | --------------------------------- | ----------------- |
| `/api/v1/pinpad/cobrar`             | Cobrar en el pinpad               | Obligatorio       |
| `/api/v1/pinpad/probar`             | Probar eco PCL sin cobrar         | No se usa         |
| `/api/v1/impresion/comprobante`     | Imprimir el comprobante           | Obligatorio       |
| `/api/v1/impresion/gaveta`          | Abrir la gaveta de dinero         | No se usa         |
| `/api/v1/impresion/estado`          | Diagnóstico de la impresora       | No se usa         |

Los POST sin cuerpo deben mandar `Content-Length: 0` (o un cuerpo `{}`);
`HTTP.sys` responde `411` si no hay ni cuerpo ni longitud declarada.

La prueba del pinpad usa `POST /api/v1/pinpad/probar` con el encabezado
`X-Peripheral-Token` y cuerpo vacío. Ejecuta el eco PCL (operación 20), que no
cobra ni requiere tarjeta. Devuelve `comunicacion_ok` y `respuesta_recibida`;
si el equipo no responde, incluye `mensaje_error`.

Las rutas inexistentes responden `404` y cualquier método distinto de `POST`
responde `405`, en ese orden: primero se valida el origen local, después el
token, después el método y por último la ruta.

#### Arranque en la caja

`IniciarPerifericos.cmd`, en la carpeta de despliegue, arranca el host con todo
lo que necesita: se coloca en su propia carpeta —las DLL nativas de PCL viven
ahí—, toma el token de `token.txt` y deja un rastro en `host.log`. Sin argumentos
deja la ventana abierta al terminar para que se lea el error; con `sinpausa`
—como lo invoca la tarea programada— sale sin esperar.

```
Host\bin\Release\
  token.txt                       token del servicio, una línea, 32 caracteres o más
  IniciarPerifericos.cmd          arranca el host
  RegistrarArranqueAutomatico.ps1 registra el arranque con la sesión
  Instalar.ps1                    instala, fija la impresora y verifica la caja
  LEEME.txt                       la guía que viaja con el paquete
  SmartOne.Perifericos.Host.exe   + la DLL del módulo, las DLL nativas y el .exe.config
```

Para instalar una caja nueva no hace falta copiar archivo por archivo: el
paquete de `dist\` ya trae todo y `Instalar.ps1` lo verifica. Ver
[Carpeta de despliegue para otra caja](#carpeta-de-despliegue-para-otra-caja).

El valor de `token.txt` debe ser el mismo que `PERIFERICOS_TOKEN` en FastAPI. Si
el archivo no está, el `.cmd` usa `PERIFERICOS_TOKEN` del entorno; si tampoco
existe, avisa y sale con código 1. En la caja no hay que copiarlo a mano: ver
[El token de la caja](#el-token-de-la-caja).

`RegistrarArranqueAutomatico.ps1` deja el host arrancando solo al iniciar sesión
y lo reanuda en menos de un minuto si el proceso se cae o alguien cierra la
ventana. No hace falta ejecutarlo elevado: el host escucha sólo en `127.0.0.1`,
así que no necesita reserva de URL.

```powershell
.\RegistrarArranqueAutomatico.ps1 -Token 'p0s_Caja3_2026_ClaveServicioLocal'  # escribe token.txt y registra la tarea
Start-ScheduledTask -TaskName 'SmartOne.Perifericos.Host'                     # arrancarlo ya, sin cerrar sesión
Get-ScheduledTaskInfo -TaskName 'SmartOne.Perifericos.Host'                   # LastTaskResult 267009 = sigue corriendo
.\RegistrarArranqueAutomatico.ps1 -Quitar                                     # deshacerlo
```

La tarea, `SmartOne.Perifericos.Host`, corre como el usuario que la registra y
sin privilegios, y se detiene con la sesión. Mientras el host vive, los intentos
del keep-alive no abren una segunda instancia (`MultipleInstances IgnoreNew`);
si el operador cierra la ventana, el servicio vuelve solo en el siguiente minuto.

Si `HttpListener` devolviera acceso denegado al arrancar en otra máquina —no es
el caso de un prefijo de bucle local—, registra la reserva una vez desde una
consola elevada para la cuenta que ejecutará el host:

```powershell
netsh http add urlacl url=http://127.0.0.1:5005/ user=DOMINIO\cuenta
```

#### El token de la caja

El token es un secreto simétrico: la API lo manda en `X-Peripheral-Token` y el
host lo exige en cada `POST`. El host no arranca con uno de menos de 32
caracteres (`A-Z a-z 0-9 . _ -`). Como la API de FastAPI corre en la misma PC
que el host, `Instalar.ps1` lo resuelve leyendo su `.env`, en vez de obligar a
copiar el mismo valor entre `token.txt` y `PERIFERICOS_TOKEN`. El orden es:

| Prioridad | Origen                    | Cuándo                                    |
| --------- | ------------------------- | ----------------------------------------- |
| 1         | `-Token`                  | Se pasa a mano                            |
| 2         | `-TokenAuto`              | Se quiere rotar el secreto                |
| 3         | `.env` de la API          | Declara `PERIFERICOS_TOKEN` con valor     |
| 4         | `token.txt` de la caja    | La caja ya tenía uno                      |
| 5         | Generado                  | Ni el `.env` ni la caja tenían uno        |

El valor se valida **antes** de registrar la tarea: con un token inválido el
script hace `FALLO`, termina con código 1 y no escribe `token.txt` ni arranca
nada, así que la tarea nunca queda reintentando contra un token que el host
rechazaría.

El `.env` se busca solo entre cada carpeta padre de la carpeta de despliegue y
las hermanas de esos padres, hasta tres niveles, y se reconoce porque declara
`PERIFERICOS_TOKEN`. Con `-EnvApi <ruta>` se fija la ruta exacta; con
`-SinTocarApi` no se lee ni se escribe —es el caso de una API en otra máquina—.
Antes de reescribirlo guarda el anterior como `.env.bak` al lado.

La escritura es quirúrgica: se reemplaza sólo el valor de esa línea y se
conservan el BOM si lo había, los finales de línea, un comentario al final de la
misma línea y todo el resto del archivo. Si la clave no está declarada, se
agrega al final con el salto de línea dominante del archivo.

La API lee su `.env` una sola vez, al arrancar (`pydantic_settings`), así que
después de un cambio de token hay que reiniciar la API; el instalador lo avisa.

Un `HTTP 401` en la prueba de humo con el host **ya** en marcha es un desajuste
entre el host y quien lo llama —el proceso vivo todavía tiene el `token.txt`
anterior—, no un problema de la API: se reporta como `AVISO` y se saltea la
prueba del pinpad. Basta reiniciar el host para que lea el token nuevo.

#### Cobro con pinpad (HTTP)

El JSON de solicitud es `{"monto": 25.50}` y requiere los encabezados
`X-Peripheral-Token` e `Idempotency-Key` (UUID). Repetir la misma clave y monto
mientras el host siga ejecutándose devuelve el mismo resultado sin enviar otro
cobro al equipo; usar la misma clave con otro monto responde `409`. Las claves
se conservan durante 24 horas en memoria.

#### Impresión de comprobante (HTTP)

`POST /api/v1/impresion/comprobante` recibe el comprobante completo, sin
depender de los modelos de venta del POS:

```json
{
  "venta": {
    "tipo_documento": "BOLETA DE VENTA ELECTRONICA",
    "serie_numero": "B001-00012345",
    "fecha_emision": "14/10/2026",
    "hora_emision": "09:41:00",
    "cliente_documento": "45678912",
    "cliente_razon": "TRANSPORTES CAÑERIA MUÑOZ SAC",
    "subtotal": 42.37,
    "igv": 7.63,
    "total": 50.00,
    "vuelto": 0.00,
    "condicion_pago": "CONTADO",
    "linea": {
      "descripcion": "DIESEL B5 S-50",
      "galones": 5.128,
      "precio": 9.75,
      "total": 50.00,
      "lado": 1,
      "manguera": 2,
      "placa": "ABC-123"
    },
    "pagos": [
      { "descripcion": "TARJETA VISA", "monto": 50.00, "numero_operacion": "000123456" }
    ]
  },
  "emisor": {
    "ruc": "20512345678",
    "razon_social": "GRIFO LOS PERIFERICOS SAC",
    "nombre_comercial": "GRIFO PERIFERICOS",
    "direccion": "AV. SIEMPRE VIVA 123",
    "domicilio_fiscal": "AV. SIEMPRE VIVA 123 - LIMA",
    "distrito": "LIMA",
    "provincia": "LIMA",
    "departamento": "LIMA",
    "telefono": "01-5551234"
  },
  "contexto": { "cajero": "JOSE VEGAS", "turno_numero": 1, "nro_serie_maquina": "CAJA-01" },
  "copia": false
}
```

`venta`, `emisor` y `contexto` son obligatorios, igual que `serie_numero`, el
`ruc` y la `razon_social` del emisor y la `descripcion` de la línea: no se
imprime un cargo sin nombre. `pagos` es opcional. `copia` marca el papel como
copia; se usa para la segunda impresión que pide el cliente.

La respuesta es `200`:

```json
{
  "impreso": true,
  "ya_impreso": false,
  "impresora": "EPSON TM-T20II Receipt",
  "serie_numero": "B001-00012345",
  "copia": false
}
```

Reglas de repetición:

- La impresión memoriza **sólo los éxitos**. Si la impresora está apagada o sin
  papel, la respuesta es `500` con el motivo en castellano y la misma clave
  puede reintentarse; en el cobro, en cambio, la clave se memoriza aunque falle,
  porque un cobro no se reintenta solo.
- Repetir la misma clave con el mismo comprobante y el mismo `copia` responde
  `200` con `ya_impreso: true` y **no** saca un segundo papel.
- La misma clave con otro `serie_numero` o con otro `copia` responde `409`.
- Para sacar físicamente un segundo papel hay que mandar `copia: true` con una
  clave **nueva**, así el papel adicional queda marcado como copia.
- La clave del comprobante es independiente de la del cobro, aunque sean el
  mismo UUID: se puede usar la clave de la venta para cobrar y para imprimir.
- El cuerpo máximo es 64 KB (`413` si se excede).

#### Abrir gaveta

`POST /api/v1/impresion/gaveta` recibe un cuerpo vacío y responde `200`
`{"abierta": true, "impresora": "EPSON TM-T20II Receipt"}`. No usa clave de
idempotencia: abrir dos veces la misma gaveta no deja nada a medias, y el
backend a veces sólo quiere el cajón (dar cambio, guardar un depósito) sin
venta de por medio.

#### Estado de la impresora

`POST /api/v1/impresion/estado` responde `200` aunque no haya impresora, con
`disponible: false` y el motivo. Sirve para saber desde el backend por qué no
sale un ticket sin gastar un papel:

```json
{
  "disponible": false,
  "impresora": null,
  "detalle": "No hay ninguna impresora térmica reconocible en esta caja. ...",
  "columnas": 64,
  "pagina_codigos": 19
}
```

#### Impresora de tickets

El host resuelve la impresora en cada petición (nunca la guarda en caché). Lee
`impresora_ticket` de `SmartOne.Perifericos.Host.exe.config`, el archivo que
`MSBuild` copia de `Host\App.config` y queda junto al ejecutable. En la caja se
edita ese `.exe.config`:

```xml
<add key="impresora_ticket" value="EPSON TM-T20II Receipt" />
```

Si se deja vacío, se busca una cola térmica reconocible por el nombre (`TM-T`,
`TM-U`, `EPSON TM`, `RECEIPT`, `TICKET`, `THERMAL`, `POS-`). Nunca se usa la
impresora predeterminada de Windows: en un grifo suele ser la láser de la
oficina y el ESC/POS crudo sale por ahí como varias hojas de basura.

Configura `PERIFERICOS_TOKEN` en el entorno de ambos procesos con el mismo valor
aleatorio de al menos 32 caracteres. El host rechaza el arranque si falta. La
API de FastAPI expone `POST /api/v1/pinpad/cobrar`, protegido por autenticación
y el permiso `VEN.EDIT`; envía el mismo `Idempotency-Key` en la cabecera y
`{"monto": 25.50}` en el cuerpo. Si la respuesta no viene o agota el tiempo,
consulta el pinpad y el voucher antes de reintentar. Las rutas de impresión se
usan igual: `POST /api/v1/impresion/comprobante` con el comprobante completo y
el mismo `Idempotency-Key` de la venta; repetirlo no saca un segundo papel.
Para una prueba no monetaria, FastAPI expone `POST /api/v1/pinpad/probar`,
protegido con autenticación y `VEN.VER`; `comunicacion_ok: true` confirma que el
eco PCL recibió respuesta del equipo.

Mantén las DLL nativas PCL junto al ejecutable. Si el proceso se reinicia,
las claves de idempotencia en memoria se pierden; antes de repetir un cobro
iniciado antes del reinicio hay que conciliarlo en el terminal.

### Impresión de comprobante

`ImpresionService` recibe los datos mínimos del comprobante, del emisor y del
contexto operativo; no depende de una sesión ni de los modelos de venta del POS.

```csharp
using PuntoVenta.Models.Impresion;
using PuntoVenta.Services;

var impresora = new ImpresionService("EPSON TM-T20II Receipt");
impresora.Imprimir(venta, emisor,
    new ContextoImpresion(nombreCajero, numeroTurno, serieMaquina));
```

`venta` y `emisor` son instancias de `ComprobanteImpresion` y
`EmisorImpresion`; se asignan desde los datos del servicio consumidor.

Si se omite el nombre en el constructor, se usa `impresora_ticket` de la
configuración del proceso consumidor; si no está configurado, se busca una
cola térmica reconocible. No se usa la impresora predeterminada.

## Despliegue

- Compilar y ejecutar el servicio consumidor como **x64**.
- Copiar junto al ejecutable consumidor `PCLService.dll`, `PCL_Bridge.dll`,
  `PclUtilities.dll`, `dllsign.dll`, `msvcp110.dll` y `msvcr110.dll` desde
  `nativas`. Son dependencias nativas y no se cargan desde el ensamblado .NET.
- Instalar la impresora térmica en Windows. El módulo envía comandos ESC/POS
  en modo RAW a la cola indicada.
- Enchufar el pinpad por USB y encenderlo. Windows debe publicarlo como puerto
  serie. No hay que parearlo a mano: `AsegurarServicioEnSegundoPlano()` lo hace
  en el primer arranque y PCL recuerda el pareo en esa máquina. Para comprobar
  una caja recién instalada sin tarjeta, ver
  [Conexión con el equipo](#conexión-con-el-equipo).
- Llevar el `SmartOne.Perifericos.Host.exe.config` junto al ejecutable y poner
  ahí el nombre exacto de la cola térmica en `impresora_ticket`. Si se edita el
  `.exe.config` ya copiado, `MSBuild` no lo vuelve a sobrescribir en la
  siguiente compilación: si hay dudas, bórralo para que se regenere de
  `Host\App.config`.
- La DLL PCL, sus runtimes de Visual C++ y la impresora son requisitos de la
  máquina que ejecuta el servicio, no del servidor remoto.
- `PinpadService` escribe `pinpad.log` junto al ejecutable del proceso
  consumidor (no en el directorio de trabajo). Trae el arranque del servicio
  PCL y la trama de cada operación; es lo primero que se mira cuando el pinpad
  no responde.

### Carpeta de despliegue para otra caja

`CrearPaquete.ps1` (en `Host\Despliegue`, se queda en el repo y **no** viaja en
el paquete) compila en Release y arma `dist\SmartOne.Perifericos.Host\` con los
14 archivos que necesita una caja, más un ZIP hermano para copiar por red o
llevar en un pendrive.

```powershell
.\CrearPaquete.ps1                       # compila y empaqueta en dist\
.\CrearPaquete.ps1 -SinCompilar          # reusa lo último compilado
.\CrearPaquete.ps1 -DetenerServicio      # si el host corre en esta máquina (lo vuelve a levantar)
```

El `.exe.config` del paquete se **regenera desde `Host\App.config`**, no se copia
de `bin\Release`: así `impresora_ticket` sale vacío y el paquete no arrastra el
nombre de la impresora de la máquina donde se compiló. Tampoco lleva
`token.txt`, `*.log` ni `*.pdb`, y `version.txt` deja constancia del commit, la
rama y la fecha de armado.

En la caja, el paquete se instala y se verifica con `Instalar.ps1`, que es
idempotente y no necesita consola elevada:

```powershell
Get-ChildItem | Unblock-File                  # sólo si el ZIP vino de otra máquina o de internet
.\Instalar.ps1                                # toma el PERIFERICOS_TOKEN del .env y sincroniza los dos lados
.\Instalar.ps1 -Token 'p0s_Caja3_2026_ClaveServicioLocal'   # forzar un valor concreto
.\Instalar.ps1 -TokenAuto                     # rotar el secreto en los dos lados
.\Instalar.ps1 -EnvApi 'D:\api\.env'          # si el .env no está entre las carpetas vecinas
.\Instalar.ps1 -SinTocarApi                   # API en otra máquina: genera y muestra el token
.\Instalar.ps1 -Impresora 'EPSON TM-T20II Receipt'   # fijar la cola si no se autodetecta
.\Instalar.ps1 -SinArrancar                   # revisar sin levantar el host
.\Instalar.ps1 -Quitar                        # detener y desinstalar de esta caja
```

Hace cinco pasos y termina con un informe; devuelve `exit 1` si algo quedó en
`FALLO`, no si quedó en `AVISO`:

1. Requisitos: Windows x64, .NET Framework 4.8 (`Release` 528040 o más) y los
   10 archivos obligatorios del paquete. El `.exe.config` es un `AVISO` aparte:
   sin él no se puede fijar la impresora con `-Impresora`.
2. Periféricos: busca la cola térmica entre las instaladas con el mismo patrón
   de nombres que usa el módulo, y el pinpad por PnP (`VID_0B00`, Ingenico) o
   por la existencia de algún puerto serie. Aquí sólo avisa: si falta el
   periférico, se instala después y no hay que repetir el paso.
3. Impresora fijada con `-Impresora`: si el nombre no existe en esa máquina es
   `FALLO` y no se registra ni se arranca nada. Un nombre elegido a mano se
   toma como decisión deliberada, no como algo a adivinar.
4. Token y arranque: resuelve el token como se describe en
   [El token de la caja](#el-token-de-la-caja) —sincronizando el `.env` de la
   API si hace falta—, escribe `token.txt` y delega el registro de la tarea en
   `RegistrarArranqueAutomatico.ps1`; no duplica esa lógica. Un token inválido
   es `FALLO` y no se registra ni se arranca nada.
5. Arranque y prueba de humo: espera a que el puerto escuche y consulta
   `/api/v1/impresion/estado` y `/api/v1/pinpad/probar`. Un `401` con el host
   ya en marcha es `AVISO` —el proceso vivo sigue con el token anterior y hay
   que reiniciarlo— y además se saltea la prueba del pinpad.

`-TokenAuto` genera 32 bytes al azar en base64url (43 caracteres, dentro del
alfabeto que valida el registrador) y lo imprime. La carpeta viaja **sin**
`token.txt`: el token se decide en la caja. En el caso normal —la API en la
misma máquina— no hay nada que pegar: el instalador adopta el token del `.env`
y, si la API todavía no tiene uno, lo escribe él mismo en los dos lados.
