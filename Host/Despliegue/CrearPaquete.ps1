<#
.SYNOPSIS
    Arma la carpeta de despliegue para llevar los periféricos a otra caja.

.DESCRIPTION
    Compila la solución en Release y copia a una carpeta limpia --sin bitácoras,
    sin token y sin símbolos-- todo lo que la caja destino necesita: el host, la
    DLL del módulo, las seis DLL nativas de Ingenico y los scripts de
    instalación. El .exe.config se regenera desde Host\App.config, de modo que
    nunca arrastra la impresora de la máquina donde se compila.

    Al terminar deja también un .zip listo para copiar en una memoria USB.

.PARAMETER Destino
    Carpeta donde se arma el paquete. Por defecto, dist\ en la raíz del repo.

.PARAMETER SinCompilar
    Reutiliza lo ya compilado en Host\bin\Release en vez de compilar.

.PARAMETER SinZip
    No genera el archivo .zip.

.PARAMETER DetenerServicio
    Si el host está en ejecución, lo detiene antes de compilar y lo vuelve a
    levantar al terminar. Sin esta marca, el script falla avisando, porque
    MSBuild no puede sobrescribir un .exe en uso.

.EXAMPLE
    .\CrearPaquete.ps1 -DetenerServicio
#>
[CmdletBinding()]
param(
    [string] $Destino,
    [switch] $SinCompilar,
    [switch] $SinZip,
    [switch] $DetenerServicio
)

$ErrorActionPreference = 'Stop'

$despliegue   = $PSScriptRoot
$hostDir      = Split-Path -Path $despliegue -Parent
$repo         = Split-Path -Path $hostDir -Parent
$salida       = Join-Path $hostDir 'bin\Release'
$nombrePaquete = 'SmartOne.Perifericos.Host'
$nombreTarea  = 'SmartOne.Perifericos.Host'

if (-not $Destino) { $Destino = Join-Path $repo 'dist' }
$raizPaquete = Join-Path $Destino $nombrePaquete

function Buscar-MSBuild {
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ($programFilesX86) {
        $vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path -LiteralPath $vswhere) {
            $encontrado = & $vswhere -latest -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null |
                Select-Object -First 1
            if ($encontrado -and (Test-Path -LiteralPath $encontrado)) { return $encontrado }
        }
    }

    $comando = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($comando) { return $comando.Source }

    return $null
}

Write-Host ''
Write-Host '  ===============================================================' -ForegroundColor White
Write-Host '   Paquete de despliegue de periféricos' -ForegroundColor White
Write-Host ('   Destino: ' + $raizPaquete) -ForegroundColor DarkGray
Write-Host '  ===============================================================' -ForegroundColor White

# --------------------------------------------------------- host en ejecución --
$procesos = @(Get-Process -Name $nombreTarea -ErrorAction SilentlyContinue)
$reanudarServicio = $false

if ($procesos.Count -gt 0 -and -not $SinCompilar) {
    if (-not $DetenerServicio) {
        throw ('El host está en ejecución y MSBuild no puede sobrescribir el .exe. ' +
            'Vuelva a ejecutar con -DetenerServicio, o deténgalo antes: ' +
            'Stop-ScheduledTask -TaskName ''' + $nombreTarea + '''; Disable-ScheduledTask -TaskName ''' + $nombreTarea + '''')
    }

    Write-Host ''
    Write-Host '  Deteniendo el servicio para poder compilar...' -ForegroundColor Yellow
    Stop-ScheduledTask -TaskName $nombreTarea -ErrorAction SilentlyContinue
    Disable-ScheduledTask -TaskName $nombreTarea -ErrorAction SilentlyContinue | Out-Null
    foreach ($p in @(Get-Process -Name $nombreTarea -ErrorAction SilentlyContinue)) {
        Stop-Process -Id $p.Id
    }
    Start-Sleep -Seconds 3
    $reanudarServicio = $true
}

# ------------------------------------------------------------------ compilar --
if (-not $SinCompilar) {
    $msbuild = Buscar-MSBuild
    if (-not $msbuild) {
        throw 'No se encontró MSBuild. Abra una consola de desarrollador de Visual Studio o use -SinCompilar.'
    }

    Write-Host ''
    Write-Host ('  Compilando Release con ' + $msbuild) -ForegroundColor Cyan
    & $msbuild (Join-Path $repo 'SmartOne.Perifericos.slnx') /p:Configuration=Release /v:minimal /nologo
    if ($LASTEXITCODE -ne 0) {
        throw ('La compilación terminó con código ' + $LASTEXITCODE + '.')
    }
}

# ------------------------------------------------------------------ armar -----
$desdeSalida = @(
    'SmartOne.Perifericos.Host.exe',
    'SmartOne.Perifericos.dll',
    'PCLService.dll',
    'PCL_Bridge.dll',
    'PclUtilities.dll',
    'dllsign.dll',
    'msvcp110.dll',
    'msvcr110.dll'
)

$desdeDespliegue = @(
    'IniciarPerifericos.cmd',
    'RegistrarArranqueAutomatico.ps1',
    'Instalar.ps1',
    'LEEME.txt'
)

$faltantes = @(@($desdeSalida | ForEach-Object { Join-Path $salida $_ }) +
    @($desdeDespliegue | ForEach-Object { Join-Path $despliegue $_ }) +
    @((Join-Path $hostDir 'App.config')) |
    Where-Object { -not (Test-Path -LiteralPath $_) })

if ($faltantes.Count -gt 0) {
    throw ('Faltan archivos por copiar: ' + ($faltantes -join ', '))
}

if (Test-Path -LiteralPath $raizPaquete) {
    Remove-Item -LiteralPath $raizPaquete -Recurse -Force
}
New-Item -ItemType Directory -Path $raizPaquete -Force | Out-Null

foreach ($nombre in $desdeSalida) {
    Copy-Item -LiteralPath (Join-Path $salida $nombre) -Destination (Join-Path $raizPaquete $nombre)
}

foreach ($nombre in $desdeDespliegue) {
    Copy-Item -LiteralPath (Join-Path $despliegue $nombre) -Destination (Join-Path $raizPaquete $nombre)
}

# El .exe.config se regenera desde App.config: así el paquete sale siempre con
# impresora_ticket vacío, sin la impresora de la máquina donde se compiló.
Copy-Item -LiteralPath (Join-Path $hostDir 'App.config') `
    -Destination (Join-Path $raizPaquete ($nombrePaquete + '.exe.config'))

# ------------------------------------------------------------------ version ---
$git = Get-Command git.exe -ErrorAction SilentlyContinue
$commit = 'desconocido'
$rama = 'desconocida'
$sucio = $false

if ($git) {
    $c = & git.exe -C $repo rev-parse --short HEAD 2>$null
    if ($c) { $commit = ($c | Select-Object -First 1).Trim() }
    $r = & git.exe -C $repo rev-parse --abbrev-ref HEAD 2>$null
    if ($r) { $rama = ($r | Select-Object -First 1).Trim() }
    $sucio = [bool] (@(& git.exe -C $repo status --porcelain 2>$null))
}

$version = @(
    ('Paquete   : ' + $nombrePaquete)
    ('Armado    : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
    ('Commit    : ' + $commit)
    ('Rama      : ' + $rama)
    ('Sin confirmar en el repo : ' + $(if ($sucio) { 'si' } else { 'no' }))
    ('Máquina   : ' + $env:COMPUTERNAME)
    ('Compilado : ' + $(if ($SinCompilar) { 'no (se reutilizó bin\Release)' } else { 'sí, Release' }))
)
$version | Set-Content -LiteralPath (Join-Path $raizPaquete 'version.txt') -Encoding UTF8

# ---------------------------------------------------------------- resumen -----
Write-Host ''
Write-Host '  Contenido del paquete:' -ForegroundColor Cyan

$items = @(Get-ChildItem -LiteralPath $raizPaquete | Sort-Object -Property Name)
$total = 0
foreach ($item in $items) {
    $total += $item.Length
    Write-Host ('   {0,-40} {1,10:N0} B' -f $item.Name, $item.Length) -ForegroundColor Gray
}
Write-Host ('   {0,-40} {1,10:N0} B' -f ($items.Count.ToString() + ' archivos'), $total) -ForegroundColor White

# --------------------------------------------------------------------- zip ----
$zip = $null
if (-not $SinZip) {
    $sello = Get-Date -Format 'yyyyMMdd-HHmm'
    $zip = Join-Path $Destino ($nombrePaquete + '-' + $sello + '.zip')

    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path $raizPaquete -DestinationPath $zip -CompressionLevel Optimal
    $zip = (Get-Item -LiteralPath $zip).FullName

    Write-Host ''
    Write-Host ('  ZIP: ' + $zip) -ForegroundColor Green
    Write-Host ('       ' + ('{0:N0}' -f (Get-Item -LiteralPath $zip).Length) + ' B') -ForegroundColor Gray
}

# ------------------------------------------------------- reanudar servicio ----
if ($reanudarServicio) {
    Enable-ScheduledTask -TaskName $nombreTarea | Out-Null
    Start-ScheduledTask -TaskName $nombreTarea
    Write-Host ''
    Write-Host '  Servicio levantado de nuevo en esta máquina.' -ForegroundColor Green
}

Write-Host ''
Write-Host '  Para instalar en la caja:' -ForegroundColor Cyan
Write-Host '   1. Copie la carpeta completa (o descomprima el ZIP) en la PC de la caja.'
Write-Host '   2. Si bajó el ZIP de otra máquina o de internet:  Get-ChildItem | Unblock-File'
Write-Host '   3. powershell -NoProfile -ExecutionPolicy Bypass -File .\Instalar.ps1'
Write-Host '      (toma el PERIFERICOS_TOKEN del .env de la API; -Token lo fuerza)'
Write-Host ''
