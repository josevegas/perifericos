<#
.SYNOPSIS
    Registra (o quita) el arranque automatico del servicio local de perifericos.

.DESCRIPTION
    Crea una tarea programada que ejecuta IniciarPerifericos.cmd al iniciar
    sesion el usuario indicado, de modo que la caja siempre tenga el pinpad y
    la impresora disponibles sin que nadie arranque nada a mano.

    No requiere permisos de administrador: el servicio escucha solo en
    127.0.0.1, asi que no necesita reserva de URL ni elevacion.

    El token se guarda en token.txt (misma carpeta). Ese archivo debe tener los
    mismos permisos que el resto del despliegue: contiene el secreto compartido
    con la API de FastAPI.

.PARAMETER Token
    Token de servicio. Se escribe en token.txt. Si se omite y token.txt ya
    existe, se reutiliza el que haya.

.PARAMETER Usuario
    Usuario cuyo inicio de sesion dispara la tarea. Por defecto, el actual.

.PARAMETER NombreTarea
    Nombre de la tarea programada.

.PARAMETER Quitar
    Elimina la tarea programada en lugar de crearla.

.EXAMPLE
    .\RegistrarArranqueAutomatico.ps1 -Token 'p0s_Caja3_2026_ClaveServicioLocal'

.EXAMPLE
    .\RegistrarArranqueAutomatico.ps1

.EXAMPLE
    .\RegistrarArranqueAutomatico.ps1 -Quitar
#>
[CmdletBinding()]
param(
    [string] $Token,
    [string] $Usuario,
    [string] $NombreTarea = 'SmartOne.Perifericos.Host',
    [switch] $Quitar
)

$ErrorActionPreference = 'Stop'

$raiz     = $PSScriptRoot
$lanzador = Join-Path $raiz 'IniciarPerifericos.cmd'
$exe      = Join-Path $raiz 'SmartOne.Perifericos.Host.exe'
$tokenTxt = Join-Path $raiz 'token.txt'

if (-not $Usuario) { $Usuario = "$env:USERDOMAIN\$env:USERNAME" }

# ---------------------------------------------------------------- quitar -----
if ($Quitar) {
    $tarea = Get-ScheduledTask -TaskName $NombreTarea -ErrorAction SilentlyContinue
    if (-not $tarea) {
        Write-Host "La tarea '$NombreTarea' no estaba registrada. Nada que quitar." -ForegroundColor Yellow
        return
    }
    Unregister-ScheduledTask -TaskName $NombreTarea -Confirm:$false
    Write-Host "Tarea '$NombreTarea' eliminada." -ForegroundColor Green
    return
}

# ------------------------------------------------------------ comprobaciones --
if (-not (Test-Path -LiteralPath $exe)) {
    throw "No se encuentra SmartOne.Perifericos.Host.exe en '$raiz'. Copie la carpeta de despliegue completa."
}
if (-not (Test-Path -LiteralPath $lanzador)) {
    throw "No se encuentra IniciarPerifericos.cmd en '$raiz'."
}

# ------------------------------------------------------------------ token ----
if ($Token) {
    if ($Token -notmatch '^[A-Za-z0-9._-]{32,}$') {
        throw 'El token debe tener 32 caracteres o mas y usar solo letras, numeros, punto, guion y guion bajo.'
    }
    [System.IO.File]::WriteAllText($tokenTxt, $Token + [Environment]::NewLine, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Token escrito en '$tokenTxt'." -ForegroundColor Green
}
elseif (Test-Path -LiteralPath $tokenTxt) {
    $leido = (Get-Content -LiteralPath $tokenTxt -Raw).Trim()
    if ($leido -notmatch '^[A-Za-z0-9._-]{32,}$') {
        throw "El token de '$tokenTxt' no es valido (32 caracteres o mas, solo letras, numeros, punto, guion y guion bajo)."
    }
    Write-Host "Se reutiliza el token de '$tokenTxt'." -ForegroundColor Green
}
else {
    throw "Falta el token. Ejecute otra vez con -Token '<32 caracteres o mas>' o cree token.txt a mano."
}

# ------------------------------------------------------- tarea programada ----
$accion = New-ScheduledTaskAction -Execute $lanzador -Argument 'sinpausa' -WorkingDirectory $raiz

# Al iniciar sesion arranca de inmediato. Ademas, un disparador unico con
# repeticion cada minuto vuelve a intentarlo sin parar: con MultipleInstances
# IgnoreNew el reintento no crea una segunda instancia, asi que esto es un
# "keep-alive". Si el operador cierra la ventana o el proceso se cae, el
# servicio vuelve solo en menos de un minuto.
# (La repeticion no se puede colgar del disparador de inicio de sesion: ese
#  disparador no tiene StartBoundary y Task Scheduler la ignora. Y en este
#  PowerShell la repeticion solo existe en el conjunto de parametros de -Once.)
$porInicioDeSesion = New-ScheduledTaskTrigger -AtLogOn -User $Usuario

$keepAlive = New-ScheduledTaskTrigger `
    -Once -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes 1) `
    -RepetitionDuration (New-TimeSpan -Days 3650)

$disparadores = @($porInicioDeSesion, $keepAlive)

# Sin limite de tiempo: el servicio nunca termina por si solo.
$ajustes = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew

$principal = New-ScheduledTaskPrincipal -UserId $Usuario -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $NombreTarea `
    -Action $accion -Trigger $disparadores -Settings $ajustes -Principal $principal `
    -Description 'Servicio local de perifericos (pinpad Niubiz e impresora de tickets) para la API de FastAPI.' `
    -Force | Out-Null

Write-Host "Tarea '$NombreTarea' registrada para el inicio de sesion de $Usuario." -ForegroundColor Green
Write-Host "  Lanzador: $lanzador"
Write-Host "  Bitacora: $(Join-Path $raiz 'pinpad.log')"
Write-Host ''
Write-Host 'Verificacion:' -ForegroundColor Cyan
Write-Host "  Get-ScheduledTask -TaskName '$NombreTarea' | Get-ScheduledTaskInfo"
Write-Host '  (LastTaskResult 267009 = la tarea esta corriendo)'
Write-Host ''
Write-Host 'Para probarla ahora sin cerrar sesion:' -ForegroundColor Cyan
Write-Host "  Start-ScheduledTask -TaskName '$NombreTarea'"
Write-Host 'Para deshacerlo:' -ForegroundColor Cyan
Write-Host "  .\RegistrarArranqueAutomatico.ps1 -Quitar"
