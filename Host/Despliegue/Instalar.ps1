<#
.SYNOPSIS
    Instala y verifica el servicio local de periféricos en una caja.

.DESCRIPTION
    Deja una caja lista para operar. Comprueba los requisitos de la máquina
    (Windows de 64 bits, .NET Framework 4.8 y los archivos del paquete), revisa
    la cola de la impresora de tickets y el pinpad, fija la impresora en el
    .exe.config si se indica, escribe el token del servicio, registra el
    arranque automático, levanta el host y hace la prueba de humo contra el
    puerto 5005.

    El informe usa tres niveles:

      OK     el requisito se cumple.
      AVISO  la caja puede operar igual, pero conviene revisarlo.
      FALLO  hay que corregirlo; el script termina con código 1.

    No necesita permisos de administrador: el host escucha sólo en 127.0.0.1.

.PARAMETER Token
    Token del servicio. Debe ser el mismo valor que PERIFERICOS_TOKEN en la API
    de FastAPI. Si se omite y ya existe token.txt, se reutiliza.

.PARAMETER TokenAuto
    Genera un token nuevo con el generador criptográfico de Windows y lo usa.
    Sirve para una caja que se integra por primera vez: el token que imprime
    hay que copiarlo tal cual en PERIFERICOS_TOKEN de FastAPI. No se puede usar
    junto con -Token.

.PARAMETER Impresora
    Nombre exacto de la cola de la impresora térmica, tal como aparece en
    "Impresoras y escáneres". Se escribe en impresora_ticket del .exe.config.
    Si se omite, el módulo busca una cola térmica por el nombre.

.PARAMETER Usuario
    Cuenta cuyo inicio de sesión arranca el servicio. Por defecto, la actual.

.PARAMETER NombreTarea
    Nombre de la tarea programada que arranca el servicio.

.PARAMETER SinArrancar
    Comprueba y registra, pero no levanta el host ni hace la prueba de humo.

.PARAMETER Quitar
    Detiene el servicio y elimina la tarea programada de esta caja. No borra
    token.txt: para reinstalar basta con volver a ejecutar el instalador.

.EXAMPLE
    .\Instalar.ps1 -Token 'p0s_Caja3_2026_ClaveServicioLocal'

.EXAMPLE
    .\Instalar.ps1 -Token 'p0s_Caja3_2026_ClaveServicioLocal' -Impresora 'EPSON TM-T20II Receipt'

.EXAMPLE
    .\Instalar.ps1 -Quitar
#>
[CmdletBinding()]
param(
    [string] $Token,
    [switch] $TokenAuto,
    [string] $Impresora,
    [string] $Usuario,
    [string] $NombreTarea = 'SmartOne.Perifericos.Host',
    [switch] $SinArrancar,
    [switch] $Quitar
)

$ErrorActionPreference = 'Stop'

if ($Token -and $TokenAuto) {
    throw 'Use -Token o -TokenAuto, no los dos.'
}

$raiz        = $PSScriptRoot
$exe         = Join-Path $raiz 'SmartOne.Perifericos.Host.exe'
$config      = Join-Path $raiz 'SmartOne.Perifericos.Host.exe.config'
$lanzador    = Join-Path $raiz 'IniciarPerifericos.cmd'
$registrador = Join-Path $raiz 'RegistrarArranqueAutomatico.ps1'
$tokenTxt    = Join-Path $raiz 'token.txt'
$puerto      = 5005

$guion  = New-Object System.Collections.ArrayList
$fallos = 0
$avisos = 0
$paso   = 0

function Anota {
    param(
        [ValidateSet('OK', 'AVISO', 'FALLO')] [string] $Nivel,
        [string] $Texto
    )
    $color = switch ($Nivel) {
        'OK'    { 'Green' }
        'AVISO' { 'Yellow' }
        default { 'Red' }
    }
    Write-Host ('  [{0,-5}] {1}' -f $Nivel, $Texto) -ForegroundColor $color
    [void] $script:guion.Add((New-Object psobject -Property @{ Nivel = $Nivel; Texto = $Texto }))
    if ($Nivel -eq 'FALLO') { $script:fallos++ }
    elseif ($Nivel -eq 'AVISO') { $script:avisos++ }
}

function Titulo {
    param([string] $Texto)
    $script:paso++
    Write-Host ''
    Write-Host ('  ' + $script:paso + '. ' + $Texto) -ForegroundColor Cyan
}

function PuertoAbierto {
    param([int] $Numero)
    $cliente = New-Object System.Net.Sockets.TcpClient
    try {
        $cliente.Connect('127.0.0.1', $Numero)
        return $true
    }
    catch {
        return $false
    }
    finally {
        $cliente.Close()
    }
}

function Invocar-Perifericos {
    param([string] $Ruta, [string] $Clave)
    Invoke-RestMethod `
        -Method Post `
        -Uri ('http://127.0.0.1:' + $script:puerto + $Ruta) `
        -Headers @{ 'X-Peripheral-Token' = $Clave } `
        -ContentType 'application/json' `
        -Body '{}' `
        -TimeoutSec 60 `
        -UseBasicParsing
}

function MotivoDeFallo {
    param($Error)
    $respuesta = $Error.Exception.Response
    if ($respuesta) {
        $codigo = [int] $respuesta.StatusCode
        if ($codigo -eq 401) {
            return 'HTTP 401: el token no coincide con PERIFERICOS_TOKEN de la API.'
        }
        return 'HTTP ' + $codigo
    }
    return $Error.Exception.Message
}

Write-Host ''
Write-Host '  ===============================================================' -ForegroundColor White
Write-Host '   Servicio local de periféricos' -ForegroundColor White
Write-Host '   Instalación y verificación en esta caja' -ForegroundColor White
Write-Host ('   Carpeta: ' + $raiz) -ForegroundColor DarkGray
Write-Host '  ===============================================================' -ForegroundColor White

# ------------------------------------------------------------------ quitar ---
if ($Quitar) {
    Titulo 'Quitando el servicio de esta caja'

    # Primero la tarea: mientras siga registrada, el reintento automático
    # vuelve a levantar el host en menos de un minuto.
    $tarea = Get-ScheduledTask -TaskName $NombreTarea -ErrorAction SilentlyContinue
    if ($tarea) {
        Stop-ScheduledTask -TaskName $NombreTarea -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $NombreTarea -Confirm:$false
        Anota 'OK' ("Tarea '" + $NombreTarea + "' eliminada.")
    }
    else {
        Anota 'AVISO' ("La tarea '" + $NombreTarea + "' no estaba registrada.")
    }

    $hosts = @(Get-Process -Name 'SmartOne.Perifericos.Host' -ErrorAction SilentlyContinue)
    foreach ($h in $hosts) { Stop-Process -Id $h.Id }
    if ($hosts.Count -gt 0) {
        Start-Sleep -Seconds 3
        if (PuertoAbierto $puerto) {
            Anota 'FALLO' ('El puerto ' + $puerto + ' sigue ocupado por otro proceso.')
        }
        else {
            Anota 'OK' 'El host se detuvo y el puerto quedó libre.'
        }
    }
    else {
        Anota 'AVISO' 'El host no estaba en ejecución.'
    }

    Anota 'OK' 'El arranque automatico de esa cuenta ya no existe.'
    if (Test-Path -LiteralPath $tokenTxt) {
        Anota 'OK' ("Se conserva '" + $tokenTxt + "' para poder reinstalar sin volver a pedirlo.")
    }

    Write-Host ''
    Write-Host '  Para reinstalar:' -ForegroundColor Cyan
    Write-Host ('    .\Instalar.ps1 -Token ''<el mismo token>''')
    Write-Host ''
    exit 0
}

# ------------------------------------------------------- requisitos previos --
Titulo 'Requisitos de esta máquina'

if ([Environment]::Is64BitOperatingSystem) {
    Anota 'OK' ('Windows de 64 bits (' + [Environment]::OSVersion.VersionString + ').')
}
else {
    Anota 'FALLO' 'El ejecutable es x64: esta instalación de Windows es de 32 bits.'
}

$release = $null
try {
    $release = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction Stop).Release
}
catch { }

if ($release -and [int] $release -ge 528040) {
    Anota 'OK' ('.NET Framework 4.8 o superior (Release ' + $release + ').')
}
else {
    Anota 'FALLO' '.NET Framework 4.8 no está instalado. Descárguelo de https://dotnet.microsoft.com/download/dotnet-framework/net48 y vuelva a ejecutar.'
}

$obligatorios = @(
    'SmartOne.Perifericos.Host.exe',
    'SmartOne.Perifericos.dll',
    'PCLService.dll',
    'PCL_Bridge.dll',
    'PclUtilities.dll',
    'dllsign.dll',
    'msvcp110.dll',
    'msvcr110.dll',
    'IniciarPerifericos.cmd',
    'RegistrarArranqueAutomatico.ps1'
)

$faltan = @($obligatorios | Where-Object { -not (Test-Path -LiteralPath (Join-Path $raiz $_)) })
if ($faltan.Count -eq 0) {
    Anota 'OK' ('Están los ' + $obligatorios.Count + ' archivos del paquete.')
}
else {
    Anota 'FALLO' ('Faltan archivos del paquete: ' + ($faltan -join ', ') + '. Copie la carpeta completa, sin separar el .exe de las DLL nativas.')
}

if (Test-Path -LiteralPath $config) {
    Anota 'OK' 'Está el .exe.config junto al ejecutable.'
}
else {
    Anota 'AVISO' 'No está el .exe.config: la impresora se buscará por nombre y no podrá fijarse con -Impresora.'
}

# ------------------------------------------------- impresora y pinpad -------
Titulo 'Periféricos encontrados'

$colas = @(Get-CimInstance -ClassName Win32_Printer -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty Name)
# Es el mismo patrón que usa ImpresionService para reconocer una cola térmica.
$patronTermica = 'TM-T|TM-U|EPSON TM|RECEIPT|TICKET|THERMAL|POS-'

if ($Impresora) {
    if ($colas -contains $Impresora) {
        Anota 'OK' ("La cola '" + $Impresora + "' está instalada.")
    }
    else {
        Anota 'FALLO' ("No hay ninguna cola llamada '" + $Impresora + "'. Instaladas: " + ($colas -join ', ') + '.')
    }
}
else {
    $termicas = @($colas | Where-Object { $_ -match $patronTermica })
    if ($termicas.Count -eq 1) {
        Anota 'OK' ("Se usará la térmica '" + $termicas[0] + "': es la única cola que parece de tickets.")
    }
    elseif ($termicas.Count -gt 1) {
        Anota 'AVISO' ('Hay más de una cola que parece térmica (' + ($termicas -join ', ') + '). Fije la buena con -Impresora "<nombre>".')
    }
    else {
        Anota 'AVISO' ('No hay ninguna impresora térmica instalada. Instaladas: ' + ($colas -join ', ') + '. Sin ella no se imprimen comprobantes.')
    }
}

$ingenico = @()
try {
    $ingenico = @(Get-CimInstance -ClassName Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.DeviceID -like '*VID_0B00*' })
}
catch { }

$puertosSerie = @([System.IO.Ports.SerialPort]::GetPortNames())

if ($ingenico.Count -gt 0) {
    $nombres = ($ingenico | ForEach-Object { $_.Name }) -join ', '
    Anota 'OK' ('Pinpad Ingenico detectado: ' + $nombres + '.')
}
elseif ($puertosSerie.Count -gt 0) {
    Anota 'AVISO' ('No se reconoce un pinpad Ingenico por USB. Hay puertos serie (' + ($puertosSerie -join ', ') + '); el módulo intentará parearlo en el primer arranque.')
}
else {
    Anota 'AVISO' 'No hay pinpad conectado. La caja podrá cobrar sólo digitando el voucher a mano.'
}

# ----------------------------------------------------- cola en el App.config --
if ($Impresora -and $fallos -eq 0) {
    Titulo 'Fijando la impresora en el .exe.config'

    if (-not (Test-Path -LiteralPath $config)) {
        Anota 'FALLO' 'No está el .exe.config; no se puede fijar impresora_ticket.'
    }
    else {
        [xml] $documento = Get-Content -LiteralPath $config -Raw
        $nodo = $documento.SelectSingleNode('/configuration/appSettings/add[@key="impresora_ticket"]')
        if (-not $nodo) {
            Anota 'FALLO' "El .exe.config no tiene la clave 'impresora_ticket'."
        }
        elseif ($nodo.value -eq $Impresora) {
            Anota 'OK' ("impresora_ticket ya apuntaba a '" + $Impresora + "'.")
        }
        else {
            $anterior = $nodo.value
            $nodo.value = $Impresora
            $documento.Save($config)
            if ([string]::IsNullOrEmpty($anterior)) { $anterior = '(vacío)' }
            Anota 'OK' ("impresora_ticket: " + $anterior + " -> " + $Impresora + '.')
        }
    }
}

if ($fallos -gt 0) {
    Write-Host ''
    Write-Host '  Hay fallos en los requisitos: no se registra el servicio ni se arranca.' -ForegroundColor Red
    Write-Host '  Corríjalos y vuelva a ejecutar el instalador.' -ForegroundColor Red
    exit 1
}

# ------------------------------------------------------- token y arranque ----
Titulo 'Token del servicio y arranque automático'

$generado = $false

if ($TokenAuto) {
    $bytes = New-Object byte[] 32
    $azar = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $azar.GetBytes($bytes) } finally { $azar.Dispose() }

    # base64url: entra en el alfabeto que valida el registrador y viaja sin
    # escapes en un encabezado HTTP.
    $Token = [Convert]::ToBase64String($bytes).Replace('+', '-').Replace('/', '_').TrimEnd([char] '=')
    $generado = $true
}

$splat = @{ NombreTarea = $NombreTarea }
if ($Token) { $splat['Token'] = $Token }
if ($Usuario) { $splat['Usuario'] = $Usuario }

try {
    & $registrador @splat
    Anota 'OK' ("Arranque automático registrado como '" + $NombreTarea + "'.")
}
catch {
    Anota 'FALLO' ('No se pudo registrar el arranque automático: ' + $_.Exception.Message)
}

$tokenEfectivo = $Token
if (-not $tokenEfectivo -and (Test-Path -LiteralPath $tokenTxt)) {
    $tokenEfectivo = (Get-Content -LiteralPath $tokenTxt -Raw).Trim()
    Anota 'OK' 'Se reutilizó el token que ya estaba en token.txt.'
}

if ($generado -and $fallos -eq 0) {
    Write-Host ''
    Write-Host '  Token generado para esta caja:' -ForegroundColor Yellow
    Write-Host ('   ' + $Token) -ForegroundColor Yellow
    Write-Host '  Cópielo tal cual en PERIFERICOS_TOKEN de la API de FastAPI.' -ForegroundColor Yellow
}

# ------------------------------------------------- arranque y prueba de humo --
Titulo 'Arranque y prueba de humo'

if ($SinArrancar) {
    Anota 'AVISO' 'Se pidió -SinArrancar: no se levantó el host ni se probó.'
}
elseif ($fallos -gt 0) {
    Anota 'AVISO' 'No se arrancó el host porque hay fallos pendientes.'
}
else {
    if (PuertoAbierto $puerto) {
        Anota 'OK' ('El host ya estaba escuchando en el puerto ' + $puerto + '.')
    }
    else {
        Start-ScheduledTask -TaskName $NombreTarea
        $limite = (Get-Date).AddSeconds(40)
        while (-not (PuertoAbierto $puerto) -and (Get-Date) -lt $limite) {
            Start-Sleep -Milliseconds 500
        }

        if (PuertoAbierto $puerto) {
            Anota 'OK' ('El host arrancó y escucha en 127.0.0.1:' + $puerto + '.')
        }
        else {
            Anota 'FALLO' ('El host no escucha en el puerto ' + $puerto + ' después de 40 segundos. Revise host.log y pinpad.log en esta carpeta.')
        }
    }

    if (-not $tokenEfectivo) {
        Anota 'AVISO' 'Sin token a mano: se omite la prueba de humo.'
    }
    elseif ($fallos -eq 0) {
        try {
            $estado = Invocar-Perifericos '/api/v1/impresion/estado' $tokenEfectivo
            if ($estado.disponible) {
                Anota 'OK' ("La impresora responde: '" + $estado.impresora + "', " + $estado.columnas + ' columnas.')
            }
            else {
                $detalle = ($estado.detalle -replace '\s+', ' ').Trim()
                Anota 'AVISO' ('La impresora no está disponible: ' + $detalle)
            }
        }
        catch {
            Anota 'FALLO' ('La ruta de estado falló: ' + (MotivoDeFallo $_))
        }

        try {
            $prueba = Invocar-Perifericos '/api/v1/pinpad/probar' $tokenEfectivo
            if ($prueba.comunicacion_ok) {
                Anota 'OK' 'El pinpad respondió al eco PCL (no cobra nada).'
            }
            elseif ($prueba.respuesta_recibida) {
                Anota 'AVISO' ('El pinpad respondió pero la operación no aprobó: ' + $prueba.mensaje_error)
            }
            else {
                Anota 'AVISO' ('El pinpad no respondió: ' + $prueba.mensaje_error)
            }
        }
        catch {
            Anota 'FALLO' ('La prueba del pinpad falló: ' + (MotivoDeFallo $_))
        }
    }
}

# ------------------------------------------------------------------ informe --
Write-Host ''
Write-Host '  ===============================================================' -ForegroundColor White
if ($fallos -eq 0 -and $avisos -eq 0) {
    Write-Host '   Caja lista: todo en orden.' -ForegroundColor Green
}
elseif ($fallos -eq 0) {
    Write-Host ('   Caja operativa con ' + $avisos + ' aviso(s) por revisar.') -ForegroundColor Yellow
}
else {
    Write-Host ('   ' + $fallos + ' fallo(s) y ' + $avisos + ' aviso(s).') -ForegroundColor Red
}
Write-Host '  ===============================================================' -ForegroundColor White

$pendientes = @($guion | Where-Object { $_.Nivel -ne 'OK' })
if ($pendientes.Count -gt 0) {
    Write-Host ''
    Write-Host '  Pendientes:' -ForegroundColor Cyan
    foreach ($p in $pendientes) {
        $color = 'Yellow'
        if ($p.Nivel -eq 'FALLO') { $color = 'Red' }
        Write-Host ('   - [' + $p.Nivel + '] ' + $p.Texto) -ForegroundColor $color
    }
}

Write-Host ''
Write-Host '  Bitácoras:' -ForegroundColor Cyan
Write-Host ('   ' + (Join-Path $raiz 'host.log') + '   arranques y salidas del host')
Write-Host ('   ' + (Join-Path $raiz 'pinpad.log') + ' arranque de PCL y trama de cada cobro')
Write-Host ''
Write-Host '  Comandos útiles:' -ForegroundColor Cyan
Write-Host ('   Get-ScheduledTaskInfo -TaskName ''' + $NombreTarea + '''   (LastTaskResult 267009 = está corriendo)')
Write-Host '   .\IniciarPerifericos.cmd                                 arrancarlo a mano, viendo la consola'
Write-Host '   .\Instalar.ps1 -Quitar                                   detener y desinstalar de esta caja'
Write-Host ''

if ($fallos -gt 0) { exit 1 }
exit 0
