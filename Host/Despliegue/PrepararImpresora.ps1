<#
.SYNOPSIS
    Crea en Windows la cola de impresión de la ticketera térmica.

.DESCRIPTION
    Una ticketera recién conectada por USB aparece en el Administrador de
    dispositivos, pero no siempre aparece en "Impresoras y escáneres": hasta que
    no exista la cola, el módulo no la ve y /api/v1/impresion/estado responde
    disponible:false aunque el cable, el puerto y el controlador estén bien.

    Este script crea esa cola sobre el puerto USB de la ticketera usando el
    driver de bandeja "Generic / Text Only" (prnge001.inf), que es el que deja
    pasar los bytes ESC/POS crudos sin reinterpretarlos. Es el mismo criterio
    con el que el módulo imprime: por el spooler, con datos RAW.

    Es idempotente: si la cola ya existe con ese driver, no toca nada y termina
    con código 0. Crear una cola es una operación de máquina y necesita permisos
    de administrador; el script los pide solo, abriéndose de nuevo elevado.

    No hace falta reiniciar el host después: el módulo resuelve la impresora en
    cada comprobante, leyendo las colas instaladas en ese momento.

.PARAMETER Nombre
    Nombre de la cola. Por defecto 'EPSON TM-T20III', que además de ser el
    modelo de la caja es un nombre que el módulo reconoce (busca TM-T, TM-U,
    EPSON TM, RECEIPT, TICKET, THERMAL, POS-; en ese orden). Si la caja tiene
    otra ticketera, ponga el nombre que la identifique.

.PARAMETER Puerto
    Puerto al que se cuelga la cola. Por defecto se toma el primer puerto USB
    que exista y esté libre. Un nombre que no exista se crea como puerto local,
    para cuando la ticketera cuelga de otro sitio (LPT1, por ejemplo).

.PARAMETER Driver
    Driver con el que se crea la cola. Por defecto 'Generic / Text Only'.

.PARAMETER Forzar
    Rehace la cola aunque ya exista con otro driver u otro puerto.

.PARAMETER Quitar
    Elimina la cola. El driver queda instalado.

.EXAMPLE
    .\PrepararImpresora.ps1

    Crea 'EPSON TM-T20III' en el primer USB libre, pidiendo UAC si hace falta.

.EXAMPLE
    .\PrepararImpresora.ps1 -Nombre 'EPSON TM-T20III' -Puerto 'USB001'

.EXAMPLE
    .\PrepararImpresora.ps1 -Quitar

.NOTES
    Este script acompaña al paquete del host. Si falta, la cola se puede crear a
    mano: Agregar impresora -> "Agregar una impresora local" -> puerto USB001 ->
    controlador "Generic / Text Only".
#>
[CmdletBinding()]
param(
    [string] $Nombre = 'EPSON TM-T20III',
    [string] $Puerto,
    [string] $Driver = 'Generic / Text Only',
    [switch] $Forzar,
    [switch] $Quitar
)

$ErrorActionPreference = 'Stop'

$script:fallos = 0
$script:avisos = 0

function Anota {
    param(
        [ValidateSet('OK', 'AVISO', 'FALLO')] [string] $Nivel,
        [string] $Texto
    )

    $color = 'Red'
    if ($Nivel -eq 'OK') { $color = 'Green' }
    elseif ($Nivel -eq 'AVISO') { $color = 'Yellow' }

    if ($Nivel -eq 'OK') { }
    elseif ($Nivel -eq 'AVISO') { $script:avisos++ }
    else { $script:fallos++ }

    Write-Host ('   [' + $Nivel + '] ' + $Texto) -ForegroundColor $color
}

function Titulo {
    param([string] $Texto)
    Write-Host ''
    Write-Host ('  ' + $Texto) -ForegroundColor Cyan
    Write-Host ('  ' + ('-' * $Texto.Length)) -ForegroundColor DarkGray
}

function EstadoFinal {
    Titulo 'Estado de la cola'

    $cola = Get-Printer -Name $Nombre -ErrorAction SilentlyContinue

    if ($cola) {
        $trabajos = @(Get-PrintJob -PrinterName $Nombre -ErrorAction SilentlyContinue).Count
        Anota 'OK' ('Cola ' + $cola.Name + ': driver ' + $cola.DriverName + ', puerto ' + $cola.PortName + ', ' + $cola.PrinterStatus + ', ' + $trabajos + ' trabajo(s) en curso.')
    }
    else {
        Anota 'FALLO' ('La cola ' + $Nombre + ' no existe: el módulo no podrá imprimir.')
    }

    Write-Host ''
    Write-Host '  Con el host en marcha, esto tiene que responder disponible:true:' -ForegroundColor Gray
    Write-Host '    Invoke-RestMethod http://127.0.0.1:5005/api/v1/impresion/estado -Method Post' -ForegroundColor Gray
    Write-Host ''
}

# ------------------------------------------------------------- elevación -----

$identidad = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identidad)

if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host ''
    Write-Host '  Crear una cola de impresión necesita permisos de administrador.' -ForegroundColor Yellow
    Write-Host '  Se vuelve a abrir este script elevado: acepte el aviso de Windows.' -ForegroundColor Yellow

    $reintento = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'))
    foreach ($clave in $PSBoundParameters.Keys) {
        $valor = $PSBoundParameters[$clave]
        if ($valor -is [switch]) {
            if ($valor.IsPresent) { $reintento += ('-' + $clave) }
        }
        else {
            $reintento += @(('-' + $clave), ('"' + [string] $valor + '"'))
        }
    }

    try {
        $proceso = Start-Process -FilePath 'powershell.exe' -ArgumentList $reintento -Verb RunAs -Wait -PassThru
    }
    catch {
        Write-Host ''
        Write-Host '  No se abrió la consola elevada (¿se canceló el aviso de Windows?).' -ForegroundColor Red
        exit 1
    }

    exit $proceso.ExitCode
}

Write-Host ''
Write-Host '  =================================================================' -ForegroundColor White
Write-Host '   Cola de impresión de la ticketera' -ForegroundColor White
Write-Host ('   Cola: ' + $Nombre + '   Driver: ' + $Driver) -ForegroundColor DarkGray
Write-Host '  =================================================================' -ForegroundColor White

# ------------------------------------------------------------------ quitar ---

if ($Quitar) {
    Titulo 'Quitando la cola'

    $existente = Get-Printer -Name $Nombre -ErrorAction SilentlyContinue
    if (-not $existente) {
        Anota 'OK' ('No hay ninguna cola llamada ' + $Nombre + ': no hay nada que quitar.')
    }
    else {
        Remove-Printer -Name $Nombre
        Anota 'OK' ('Cola ' + $Nombre + ' eliminada. El driver quedó instalado.')
    }

    Write-Host ''
    if ($script:fallos -gt 0) { exit 1 }
    exit 0
}

# ---------------------------------------------------------- hay que hacer? ---

Titulo 'Comprobación previa'

$cola = Get-Printer -Name $Nombre -ErrorAction SilentlyContinue
$hacer = $true

if ($cola -and -not $Forzar) {
    if ($cola.DriverName -eq $Driver -and ((-not $Puerto) -or ($cola.PortName -eq $Puerto))) {
        Anota 'OK' ('La cola ' + $Nombre + ' ya está en ' + $cola.PortName + ' con ' + $Driver + ': no hay nada que hacer.')
        $hacer = $false
    }
    else {
        Anota 'AVISO' ('La cola ' + $Nombre + ' ya existe con el driver ' + $cola.DriverName + ' en ' + $cola.PortName + '. Use -Forzar para rehacerla con ' + $Driver + ' en ' + $Puerto + '.')
        $hacer = $false
    }
}

if ($hacer) {

    # ------------------------------------------------------------ driver -----

    Titulo 'Driver de impresión'

    $listaDrivers = @(Get-PrinterDriver -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)

    if ($listaDrivers -contains $Driver) {
        Anota 'OK' ('El driver ' + $Driver + ' ya está instalado.')
    }
    else {
        $instalado = $false

        try { Add-PrinterDriver -Name $Driver; $instalado = $true }
        catch { $instalado = $false }

        if (-not $instalado) {
            # El driver genérico viene en la bandeja de Windows. Add-PrinterDriver
            # a veces no lo encuentra por nombre y hay que darlo de alta desde su INF.
            $inf = Join-Path $env:SystemRoot 'INF\prnge001.inf'
            if (Test-Path -LiteralPath $inf) {
                & pnputil.exe /add-driver $inf /install | Out-Null
                try { Add-PrinterDriver -Name $Driver; $instalado = $true }
                catch { $instalado = $false }
            }
        }

        if ($instalado) {
            Anota 'OK' ('Driver ' + $Driver + ' instalado.')
        }
        else {
            Anota 'FALLO' ('No se pudo instalar el driver ' + $Driver + '. Instale la ticketera con el controlador del fabricante y vuelva a ejecutar, o pase -Driver con el nombre exacto del que quiera usar.')
        }
    }

    # ------------------------------------------------------------ puerto -----

    Titulo 'Puerto de impresión'

    $puertos = @(Get-PrinterPort -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)

    if (-not $Puerto) {
        # Las columnas de las demás impresoras son las que ocupan puerto. La
        # nuestra no cuenta: si existe, su USB es justamente el que queremos.
        $ajenos = @(Get-Printer -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -ne $Nombre } |
            Select-Object -ExpandProperty PortName)

        $usb = @($puertos | Where-Object { $_ -match '^USB\d{3}$' } | Sort-Object)
        $libres = @($usb | Where-Object { $ajenos -notcontains $_ })

        if ($libres.Count -gt 0) {
            $Puerto = $libres[0]
            Anota 'OK' ('Se usará el puerto USB libre ' + $Puerto + '.')
        }
        elseif ($usb.Count -gt 0) {
            Anota 'FALLO' ('Todos los puertos USB están ocupados por otras impresoras (' + ($usb -join ', ') + '). Pase -Puerto con el que corresponda.')
        }
        else {
            Anota 'FALLO' 'No hay ningún puerto USB en esta máquina. Conecte la ticketera por USB y vuelva a ejecutar: Windows crea entonces el puerto USB001. Si cuelga de otro sitio, pase -Puerto "LPT1".'
        }
    }
    elseif ($puertos -contains $Puerto) {
        Anota 'OK' ('El puerto ' + $Puerto + ' ya existe.')
    }
    else {
        Add-PrinterPort -Name $Puerto
        Anota 'OK' ('Puerto local ' + $Puerto + ' creado.')
    }

    # ------------------------------------------------------------- cola ------

    Titulo 'Cola de impresión'

    if ($script:fallos -gt 0) {
        Anota 'FALLO' 'No se crea la cola mientras falte el driver o el puerto.'
    }
    else {
        # Agregar una cola puede cambiar la predeterminada de Windows. El módulo
        # nunca usa la predeterminada, pero se devuelve la que había para no
        # cambiarle la impresora a las demás aplicaciones de la caja.
        $predeterminada = Get-CimInstance -ClassName Win32_Printer -ErrorAction SilentlyContinue |
            Where-Object { $_.Default } | Select-Object -First 1 -ExpandProperty Name

        if ($cola) { Remove-Printer -Name $Nombre }

        Add-Printer -Name $Nombre -DriverName $Driver -PortName $Puerto
        Anota 'OK' ('Cola ' + $Nombre + ' creada en ' + $Puerto + ' con ' + $Driver + '.')

        $ahora = Get-CimInstance -ClassName Win32_Printer -ErrorAction SilentlyContinue |
            Where-Object { $_.Default } | Select-Object -First 1 -ExpandProperty Name

        if ($predeterminada -and $ahora -and $ahora -ne $predeterminada) {
            try {
                (New-Object -ComObject WScript.Network).SetDefaultPrinter($predeterminada)
                Anota 'OK' ('La impresora predeterminada de Windows siguió siendo ' + $predeterminada + '.')
            }
            catch {
                Anota 'AVISO' ('La predeterminada de Windows quedó en ' + $ahora + '. No afecta al módulo, que nunca la usa.')
            }
        }
    }
}

EstadoFinal

if ($script:fallos -gt 0) { exit 1 }
exit 0
