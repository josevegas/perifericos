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

    El token es un secreto compartido entre la API de FastAPI y el host, y en la
    caja los dos viven en la misma máquina. En vez de copiarlo a mano de un lado
    al otro, el instalador busca el .env de la API, adopta su PERIFERICOS_TOKEN y
    lo deja en token.txt; si la clave está vacía, genera uno y lo escribe en los
    dos lados. Con -SinTocarApi el .env no se lee ni se escribe.

    El informe usa tres niveles:

      OK     el requisito se cumple.
      AVISO  la caja puede operar igual, pero conviene revisarlo.
      FALLO  hay que corregirlo; el script termina con código 1.

    No necesita permisos de administrador: el host escucha sólo en 127.0.0.1.

.PARAMETER Token
    Token del servicio. Debe ser el mismo valor que PERIFERICOS_TOKEN en la API
    de FastAPI. Si se omite, se adopta el de la API; y si tampoco hay, si ya
    existe token.txt se reutiliza.

.PARAMETER TokenAuto
    Genera un token nuevo con el generador criptográfico de Windows y rota con
    él el PERIFERICOS_TOKEN de la API. Sirve para una caja que se integra por
    primera vez o cuando hay que cambiar el secreto. No se puede usar junto con
    -Token.

.PARAMETER EnvApi
    Ruta del .env de la API de FastAPI. Por defecto se busca solo, entre las
    carpetas vecinas a esta. Si el archivo no existe, es un fallo.

.PARAMETER SinTocarApi
    No lee ni escribe el .env de la API: el token sale sólo de -Token, -TokenAuto
    o token.txt. Para cuando la API vive en otra máquina y su .env se alinea a
    mano.

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
    .\Instalar.ps1
    Toma el token del .env de la API y deja la caja operando.

.EXAMPLE
    .\Instalar.ps1 -Token 'p0s_Caja3_2026_ClaveServicioLocal'

.EXAMPLE
    .\Instalar.ps1 -TokenAuto -Impresora 'EPSON TM-T20II Receipt'

.EXAMPLE
    .\Instalar.ps1 -EnvApi 'D:\api\.env' -SinArrancar

.EXAMPLE
    .\Instalar.ps1 -Quitar
#>
[CmdletBinding()]
param(
    [string] $Token,
    [switch] $TokenAuto,
    [string] $EnvApi,
    [switch] $SinTocarApi,
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

if ($EnvApi -and $SinTocarApi) {
    throw 'Use -EnvApi o -SinTocarApi, no los dos: el primero indica qué .env tocar y el segundo pide no tocarlo.'
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
$hostIniciadoAqui = $false

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
            $texto = 'HTTP 401: el token no coincide con el que tiene el host en marcha.'
            if (-not $script:hostIniciadoAqui) {
                $texto += ' El host ya estaba corriendo: reinícielo para que lea el token.txt nuevo.'
            }
            return $texto
        }
        return 'HTTP ' + $codigo
    }
    return $Error.Exception.Message
}

function NuevoToken {
    # 32 bytes al azar en base64url: entra en el alfabeto que valida el
    # registrador y viaja sin escapes en un encabezado HTTP.
    $bytes = New-Object byte[] 32
    $azar = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $azar.GetBytes($bytes) } finally { $azar.Dispose() }
    return [Convert]::ToBase64String($bytes).Replace('+', '-').Replace('/', '_').TrimEnd([char] '=')
}

function BuscarEnvApi {
    # El .env de la API se reconoce porque declara PERIFERICOS_TOKEN. Se busca en
    # el árbol cercano —cada carpeta padre y sus hermanas, hasta tres niveles—
    # porque en la caja la API y el host comparten equipo y suelen quedar vecinos.
    param([string] $Desde = $raiz)

    if (-not $Desde) { $Desde = (Get-Location).Path }

    $candidatas = New-Object System.Collections.ArrayList
    $nivel = $Desde
    for ($i = 0; $i -lt 3; $i++) {
        $padre = Split-Path -Parent $nivel
        if (-not $padre) { break }
        [void] $candidatas.Add((Join-Path $padre '.env'))
        foreach ($vecina in @(Get-ChildItem -LiteralPath $padre -Directory -Force -ErrorAction SilentlyContinue)) {
            [void] $candidatas.Add((Join-Path $vecina.FullName '.env'))
        }
        $nivel = $padre
    }

    foreach ($candidata in $candidatas) {
        if (-not (Test-Path -LiteralPath $candidata -PathType Leaf)) { continue }
        $texto = Get-Content -LiteralPath $candidata -Raw -ErrorAction SilentlyContinue
        if ($texto -match '(?m)^[ \t]*PERIFERICOS_TOKEN[ \t]*=') {
            return (Resolve-Path -LiteralPath $candidata).Path
        }
    }
    return $null
}

function LeerTokenDeEnv {
    param([string] $Ruta)
    # $null si la clave no está declarada y '' si está vacía: son casos distintos
    # —a la primera hay que agregarla al final, a la segunda rellenarla—.
    $linea = $null
    foreach ($actual in @(Get-Content -LiteralPath $Ruta -ErrorAction Stop)) {
        if ($actual -match '^[ \t]*PERIFERICOS_TOKEN[ \t]*=') { $linea = $actual; break }
    }
    if ($null -eq $linea) { return $null }

    $valor = $linea.Substring($linea.IndexOf('=') + 1).Trim()

    if ($valor.StartsWith('"') -or $valor.StartsWith("'")) {
        $fin = $valor.IndexOf($valor[0], 1)
        if ($fin -gt 0) { return $valor.Substring(1, $fin - 1) }
    }

    # Un comentario al final de la línea no forma parte del valor; ningún token
    # válido lleva '#' porque el alfabeto es A-Z a-z 0-9 . _ -
    $corte = $valor.IndexOf('#')
    if ($corte -ge 0) { $valor = $valor.Substring(0, $corte) }
    return $valor.Trim()
}

function EscribirTokenEnEnv {
    param([string] $Ruta, [string] $Valor)

    $original = [System.IO.File]::ReadAllText($Ruta, [System.Text.Encoding]::UTF8)

    # Se reemplaza sólo el valor: los comentarios, los finales de línea y el
    # resto de las claves quedan tal como estaban.
    $m = [regex]::Match($original, '(?m)^([ \t]*PERIFERICOS_TOKEN[ \t]*=)[^\r\n]*')
    if ($m.Success) {
        $clave = $m.Groups[1]
        $inicioValor = $clave.Index + $clave.Length
        $comentario = ''
        $almohadilla = $m.Value.Substring($clave.Length).IndexOf('#')
        if ($almohadilla -ge 0) {
            $comentario = ' ' + $m.Value.Substring($clave.Length).Substring($almohadilla).TrimEnd()
        }
        $nuevo = $original.Substring(0, $inicioValor) + $Valor + $comentario +
            $original.Substring($m.Index + $m.Length)
    }
    else {
        $salto = "`n"
        if ($original -match "`r`n") { $salto = "`r`n" }
        if ($original.Length -gt 0 -and -not $original.EndsWith($salto)) { $original += $salto }
        $nuevo = $original + 'PERIFERICOS_TOKEN=' + $Valor + $salto
    }

    # El respaldo queda ignorado por git en el repo de la API (regla .env.*).
    Copy-Item -LiteralPath $Ruta -Destination ($Ruta + '.bak') -Force
    [System.IO.File]::WriteAllText($Ruta, $nuevo, (New-Object System.Text.UTF8Encoding($false)))
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

# El token es compartido: la API lo manda en X-Peripheral-Token y el host lo
# exige. Antes de registrar la tarea hay que saber cuál es el bueno.
$rutaEnv  = $null
$tokenEnv = $null

if (-not $SinTocarApi) {
    if ($EnvApi) {
        if (Test-Path -LiteralPath $EnvApi -PathType Leaf) { $rutaEnv = (Resolve-Path -LiteralPath $EnvApi).Path }
    }
    else {
        $rutaEnv = BuscarEnvApi
    }

    if ($rutaEnv) {
        $tokenEnv = LeerTokenDeEnv -Ruta $rutaEnv
        Anota 'OK' ('API: ' + $rutaEnv)
        if (-not $tokenEnv) { Anota 'AVISO' 'La API todavía no tiene PERIFERICOS_TOKEN; se creará al final.' }
    }
    elseif (-not $Token -and -not $TokenAuto) {
        Anota 'AVISO' 'No se encontró el .env de la API. Indique -EnvApi <ruta>, o use -Token / -TokenAuto.'
    }
}

$generado      = $false
$adoptado      = $false
$tokenEfectivo = $null
$tokenTxtActual = $null
if (Test-Path -LiteralPath $tokenTxt -PathType Leaf) { $tokenTxtActual = (Get-Content -LiteralPath $tokenTxt -Raw).Trim() }

if ($Token) {
    $tokenEfectivo = $Token
    Anota 'OK' 'Token tomado del parámetro -Token.'
}
elseif ($TokenAuto) {
    $tokenEfectivo = NuevoToken
    $generado = $true
    Anota 'OK' 'Token generado (aleatorio de 256 bits).'
}
elseif (-not $SinTocarApi -and $tokenEnv) {
    $tokenEfectivo = $tokenEnv
    $adoptado = $true
    Anota 'OK' 'Token adoptado del .env de la API: los dos lados quedan iguales.'
}
elseif ($tokenTxtActual) {
    $tokenEfectivo = $tokenTxtActual
    Anota 'OK' 'Token reutilizado del token.txt de esta caja.'
}
elseif (-not $SinTocarApi -and $rutaEnv) {
    # La API está en esta máquina pero aún sin token: se genera uno y se
    # escribe en los dos lados para que nadie lo copie a mano.
    $tokenEfectivo = NuevoToken
    $generado = $true
    Anota 'OK' 'La API no tiene token: se genera uno y se escribe en los dos lados.'
}
else {
    $tokenEfectivo = NuevoToken
    $generado = $true
    Anota 'OK' 'Token generado (aleatorio de 256 bits).'
}

if ($tokenEfectivo.Length -lt 32 -or $tokenEfectivo -notmatch '^[A-Za-z0-9._-]+$') {
    Anota 'FALLO' 'El token debe tener 32 caracteres o más, de A-Z a-z 0-9 . _ -'
}

# Sólo se registra el arranque con un token válido; si no, la tarea arrancaría
# y moriría cada minuto mientras el registro de tareas va apuntando el fallo.
if ($fallos -eq 0) {
    $splat = @{ NombreTarea = $NombreTarea }
    $splat['Token'] = $tokenEfectivo
    if ($Usuario) { $splat['Usuario'] = $Usuario }

    try {
        & $registrador @splat
        Anota 'OK' ("Arranque automático registrado como '" + $NombreTarea + "'.")
    }
    catch {
        Anota 'FALLO' ('No se pudo registrar el arranque automático: ' + $_.Exception.Message)
    }
}

if ($fallos -gt 0) {
    Write-Host ''
    Write-Host '  El token quedó sin resolver: no se arranca el host.' -ForegroundColor Red
    Write-Host '  Corríjalo y vuelva a ejecutar el instalador.' -ForegroundColor Red
    exit 1
}

# Igualar la API. Va después de token.txt para que nunca quede una caja con el
# host listo y la API apuntando a un token viejo.
if (-not $SinTocarApi -and $rutaEnv) {
    if ($tokenEnv -ne $tokenEfectivo) {
        try {
            EscribirTokenEnEnv -Ruta $rutaEnv -Valor $tokenEfectivo
            Anota 'OK' 'PERIFERICOS_TOKEN escrito en el .env de la API (respaldo: .env.bak).'
            Anota 'AVISO' 'Reinicie la API de FastAPI: pydantic lee PERIFERICOS_TOKEN sólo al arrancar.'
        }
        catch {
            Anota 'FALLO' ('No se pudo escribir PERIFERICOS_TOKEN en el .env: ' + $_.Exception.Message)
        }
    }
    else {
        Anota 'OK' 'El PERIFERICOS_TOKEN del .env ya coincide con el del host.'
    }
}

if ($generado) {
    Write-Host ''
    Write-Host '  Token de esta caja:' -ForegroundColor Yellow
    Write-Host ('   ' + $tokenEfectivo) -ForegroundColor Yellow
    if ($rutaEnv -and $tokenEnv -eq $tokenEfectivo) {
        Write-Host '  Ya quedó escrito en el .env de la API.' -ForegroundColor Yellow
    }
    else {
        Write-Host '  Cópielo tal cual en PERIFERICOS_TOKEN de la API de FastAPI.' -ForegroundColor Yellow
    }
}

if ($adoptado) { Write-Host '  Token tomado de la API; no hay nada que copiar.' -ForegroundColor DarkGray }

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
        $hostIniciadoAqui = $true
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
        # Un 401 aquí significa que el host vivo tiene otro token del que
        # acabamos de dejar en token.txt. Si lo arrancamos nosotros, es un fallo
        # real; si ya estaba corriendo, basta con reiniciarlo.
        $aviso401 = $false

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
            if (-not $hostIniciadoAqui -and -not $aviso401 -and $_.Exception.Response -and
                [int] $_.Exception.Response.StatusCode -eq 401) {
                $aviso401 = $true
                Anota 'AVISO' 'El host en marcha todavía usa el token anterior: reinícielo para probarlo con el nuevo.'
            }
            else {
                Anota 'FALLO' ('La ruta de estado falló: ' + (MotivoDeFallo $_))
            }
        }

        if (-not $aviso401) {
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
