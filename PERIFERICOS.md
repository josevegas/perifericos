# Módulo de pinpad e impresora

Este proyecto genera `SmartOne.Perifericos.dll` para servicios Windows de
.NET Framework 4.8. El ensamblado expone el cobro Niubiz/Ingenico mediante PCL
y la impresión de comprobantes ESC/POS por la cola de impresión de Windows.
También incluye un host HTTP local, `SmartOne.Perifericos.Host.exe`, para que
FastAPI solicite cobros en el pinpad conectado a esta misma máquina.

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

### Host local para FastAPI

Compila `Host\SmartOne.Perifericos.Host.csproj` en Release x64 y ejecuta
`SmartOne.Perifericos.Host.exe` en la misma PC que ejecuta FastAPI. Escucha
únicamente en `127.0.0.1:5005`, en `POST /api/v1/pinpad/cobrar`.

El JSON de solicitud es `{"monto": 25.50}` y requiere los encabezados
`X-Peripheral-Token` e `Idempotency-Key` (UUID). Repetir la misma clave y monto
mientras el host siga ejecutándose devuelve el mismo resultado sin enviar otro
cobro al equipo; usar la misma clave con otro monto responde `409`. Las claves
se conservan durante 24 horas en memoria.

Configura `PERIFERICOS_TOKEN` en el entorno de ambos procesos con el mismo valor
aleatorio de al menos 32 caracteres. El host rechaza el arranque si falta. La
API de FastAPI expone `POST /api/v1/pinpad/cobrar`, protegido por autenticación
y el permiso `VEN.EDIT`; envía el mismo `Idempotency-Key` en la cabecera y
`{"monto": 25.50}` en el cuerpo. Si la respuesta no viene o agota el tiempo,
consulta el pinpad y el voucher antes de reintentar.

`HttpListener` necesita que Windows autorice el prefijo. Registra la reserva
una vez, desde una consola elevada, para la cuenta que ejecutará el host:

```powershell
netsh http add urlacl url=http://127.0.0.1:5005/ user=DOMINIO\cuenta
```

Inicia el host al arrancar Windows (por ejemplo, con el Programador de tareas)
y mantén las DLL nativas PCL junto al ejecutable. Si el proceso se reinicia,
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
- La DLL PCL, sus runtimes de Visual C++ y la impresora son requisitos de la
  máquina que ejecuta el servicio, no del servidor remoto.
